using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Jellyfin.Plugin.SubtitleForge.Whisper;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Translation;

/// <summary>
/// Translates an existing subtitle of another language offline (e.g. the official German subtitle into English).
/// Keeps the original timing and has no speech-recognition errors, so it is preferred over Whisper.
/// </summary>
public partial class SubtitleTranslator
{
    /// <summary>Gets the file name tag prefix of translated subtitles (in the display language).</summary>
    public static string TagPrefix => Loc.T("[AI] Translated", "[AI] Übersetzt");

    private static readonly HashSet<string> TextCodecs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa" };
    private readonly TranslationService _translation;
    private readonly FfmpegRunner _ffmpeg;
    private readonly SubtitleFileWriter _writer;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<SubtitleTranslator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleTranslator"/> class.
    /// </summary>
    /// <param name="translation">Translation service.</param>
    /// <param name="ffmpeg">ffmpeg.</param>
    /// <param name="writer">Writer.</param>
    /// <param name="paths">Paths.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleTranslator(TranslationService translation, FfmpegRunner ffmpeg, SubtitleFileWriter writer, IApplicationPaths paths, ILogger<SubtitleTranslator> logger)
    {
        _translation = translation;
        _ffmpeg = ffmpeg;
        _writer = writer;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Whether a file name tag marks a translated subtitle.
    /// </summary>
    /// <param name="tag">Tag.</param>
    /// <returns>True for translations.</returns>
    public static bool IsTranslationTag(string tag) =>
        tag.Contains("[AI] Übersetzt", StringComparison.OrdinalIgnoreCase) || tag.Contains("[AI] Translated", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the best source subtitle for a target language: official embedded text first, then OCR of the disc
    /// subtitle, then other files; never machine-made ones (Whisper, earlier translations). Direct language pairs
    /// (de↔en, ja↔en) win over the detour via English.
    /// </summary>
    /// <param name="streams">Streams of the video.</param>
    /// <param name="target">Target language (ISO 639-1).</param>
    /// <returns>Source stream and its language, or null.</returns>
    public static (MediaStream Stream, string Language)? PickSource(IReadOnlyList<MediaStream> streams, string target)
    {
        return streams
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsForced)
            .Select(s => (Stream: s, Language: Lang.ToTwoLetter(s.Language)))
            .Where(x => x.Language is not null && x.Language != target && TranslationModels.Route(x.Language, target) is not null)
            .Where(x => x.Stream.IsExternal
                ? x.Stream.Path is { } p && (p.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".ass", StringComparison.OrdinalIgnoreCase))
                    && !WhisperService.IsWhisperTag(Path.GetFileNameWithoutExtension(p)) && !IsTranslationTag(Path.GetFileNameWithoutExtension(p))
                : TextCodecs.Contains(x.Stream.Codec ?? string.Empty))
            .OrderBy(x => !x.Stream.IsExternal ? 0 : SubtitleFileWriter.TagOf(Path.GetFileNameWithoutExtension(x.Stream.Path!)) == Ocr.OcrTask.FileTag ? 1 : 2)
            .ThenBy(x => TranslationModels.Route(x.Language!, target)!.Count)
            .ThenByDescending(x => x.Language == "de" || x.Language == "en")
            .Select(x => ((MediaStream, string)?)(x.Stream, x.Language!))
            .FirstOrDefault();
    }

    /// <summary>
    /// Translates the best existing subtitle into the target language and saves it.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="target">Target language (ISO 639-1).</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Outcome.</returns>
    public async Task<WhisperOutcome> TranslateAsync(Video video, string target, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.TranslationEnabled)
        {
            return new WhisperOutcome(false, Loc.T("Translation is turned off", "Übersetzung ist ausgeschaltet"));
        }

        var streams = video.GetMediaStreams();
        if (PickSource(streams, target) is not { } source)
        {
            return new WhisperOutcome(false, Loc.German ? $"kein übersetzbarer Untertitel für {target} vorhanden" : $"no translatable subtitle for {target} available");
        }

        var cues = await ReadAsync(video, streams, source.Stream, cancellationToken).ConfigureAwait(false);
        if (cues.Count < 3)
        {
            return new WhisperOutcome(false, Loc.T("Source subtitle is empty", "Quell-Untertitel ist leer"));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var italic = cues.Select(c => ItalicRegex().IsMatch(c.Text)).ToList();
        var texts = cues.Select(c => TagRegex().Replace(c.Text, string.Empty).Replace('\n', ' ').Trim()).ToList();
        var dataDir = Plugin.Instance?.DataFolderPath ?? _paths.TempDirectory;
        var threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
        var translated = await _translation.TranslateAsync(texts, source.Language, target, Path.Combine(dataDir, "translation-models"), threads, progress, cancellationToken).ConfigureAwait(false);
        var result = new List<SubtitleCue>(cues.Count);
        for (var i = 0; i < cues.Count; i++)
        {
            var t = translated[i].Trim();
            if (t.Length == 0)
            {
                continue;
            }

            var text = string.Join('\n', WhisperPostProcessor.Wrap(t));
            result.Add(cues[i] with { Text = italic[i] ? "<i>" + text + "</i>" : text });
        }

        var stem = SubtitleFileWriter.BuildStem(video, Lang.ToThreeLetter(target), false, $"{TagPrefix} {source.Language}-{target}");
        var path = await _writer.SaveAsync(video, stem, Srt.Write(result), cancellationToken).ConfigureAwait(false);

        // The same translation under the tag of the other display language is replaced.
        var pair = $" {source.Language}-{target}";
        _writer.DeleteOwn(video, Lang.ToThreeLetter(target), tag => IsTranslationTag(tag) && tag.EndsWith(pair, StringComparison.Ordinal), path);
        var message = Loc.German
            ? FormattableString.Invariant($"{result.Count} Untertitel aus {Describe(source.Stream)} ({source.Language}) nach {target} übersetzt in {sw.Elapsed.TotalMinutes:F1} min")
            : FormattableString.Invariant($"{result.Count} subtitles translated from {Describe(source.Stream)} ({source.Language}) to {target} in {sw.Elapsed.TotalMinutes:F1} min");
        _logger.LogInformation("SubtitleForge: {Message} → {Path}", message, path);
        return new WhisperOutcome(true, message, path);
    }

    private static string Describe(MediaStream s) => s.IsExternal ? Path.GetFileName(s.Path) ?? "file" : Loc.T("embedded track", "eingebetteter Spur");

    private async Task<List<SubtitleCue>> ReadAsync(Video video, IReadOnlyList<MediaStream> streams, MediaStream stream, CancellationToken cancellationToken)
    {
        if (stream.IsExternal)
        {
            var bytes = await File.ReadAllBytesAsync(stream.Path!, cancellationToken).ConfigureAwait(false);
            return SubtitleContent.Parse(bytes, Path.GetFileName(stream.Path), null, null) ?? new List<SubtitleCue>();
        }

        var ass = stream.Codec is "ass" or "ssa";
        var tempDir = Path.Combine(_paths.TempDirectory, "subtitleforge");
        Directory.CreateDirectory(tempDir);
        var tmp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + (ass ? ".ass" : ".srt"));
        try
        {
            await _ffmpeg.ExtractStreamAsync(video.Path, StreamMap.Specifier(streams, stream), ass ? "ass" : "srt", tmp, cancellationToken).ConfigureAwait(false);
            var content = await File.ReadAllTextAsync(tmp, cancellationToken).ConfigureAwait(false);
            return ass
                ? AssConverter.Build(AssConverter.Parse(content), AssEventKind.Dialogue, AssEventKind.Song)
                : Srt.Parse(content).Select(c => c with { Text = Srt.NormalizeText(c.Text) }).ToList();
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
        }
    }

    [GeneratedRegex(@"^\s*<i>.*</i>\s*$", RegexOptions.Singleline)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
