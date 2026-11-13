using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Jellyfin.Plugin.SubtitleForge.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Whisper;

/// <summary>
/// Result of a Whisper run.
/// </summary>
/// <param name="Success">Whether a subtitle was written.</param>
/// <param name="Message">What happened.</param>
/// <param name="Path">Written file.</param>
public sealed record WhisperOutcome(bool Success, string Message, string? Path = null);

/// <summary>
/// Generates subtitles from an audio track with Whisper (CPU).
/// </summary>
public sealed class WhisperService : IDisposable
{
    /// <summary>Tag used in generated file names (track title "Whisper").</summary>
    public const string FileTag = "Whisper";

    private readonly WhisperModels _models;
    private readonly SpeechAnalyzer _speech;
    private readonly SubtitleFileWriter _writer;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WhisperService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperService"/> class.
    /// </summary>
    /// <param name="models">Model manager.</param>
    /// <param name="speech">Speech analyzer.</param>
    /// <param name="writer">Writer.</param>
    /// <param name="appPaths">App paths.</param>
    /// <param name="logger">Logger.</param>
    public WhisperService(WhisperModels models, SpeechAnalyzer speech, SubtitleFileWriter writer, IApplicationPaths appPaths, ILogger<WhisperService> logger)
    {
        _models = models;
        _speech = speech;
        _writer = writer;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Picks the audio track for a target language: a track in that language (default track first);
    /// for English optionally the original track with translation.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">ISO 639-1 target.</param>
    /// <param name="config">Config.</param>
    /// <returns>Track and whether to translate, or null.</returns>
    public static (MediaStream Audio, bool Translate)? PickAudio(Video video, string language, PluginConfiguration config)
    {
        var audio = video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).ToList();
        var same = audio.Where(a => Lang.Same(a.Language, language)).OrderByDescending(a => a.IsDefault).FirstOrDefault();
        if (same is not null)
        {
            return (same, false);
        }

        if (language == "en" && config.WhisperTranslateToEnglish && audio.Count > 0)
        {
            var original = audio.FirstOrDefault(a => a.IsDefault) ?? audio[0];
            if (Lang.ToTwoLetter(original.Language) is not null)
            {
                return (original, true);
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a subtitle.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">ISO 639-1 target language.</param>
    /// <param name="audioOverride">Specific audio track (manual action), or null for automatic choice.</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Outcome.</returns>
    public async Task<WhisperOutcome> GenerateAsync(Video video, string language, MediaStream? audioOverride, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.WhisperEnabled)
        {
            return new WhisperOutcome(false, "Whisper ist deaktiviert");
        }

        MediaStream audio;
        bool translate;
        if (audioOverride is not null)
        {
            audio = audioOverride;
            var source = Lang.ToTwoLetter(audio.Language);
            translate = source is not null && source != language;
            if (translate && language != "en")
            {
                return new WhisperOutcome(false, $"Whisper kann nur nach Englisch übersetzen (Tonspur {audio.Language} → {language})");
            }
        }
        else
        {
            var pick = PickAudio(video, language, config);
            if (pick is null)
            {
                return new WhisperOutcome(false, $"keine Tonspur in Sprache {language}");
            }

            (audio, translate) = pick.Value;
        }

        var sourceLanguage = Lang.ToTwoLetter(audio.Language) ?? "auto";
        var model = WhisperModels.Get(config.WhisperModel);
        if (translate && !model.CanTranslate)
        {
            model = WhisperModels.Get("small"); // large-v3-turbo cannot translate
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tempDir = Path.Combine(_appPaths.TempDirectory, "subtitleforge");
        Directory.CreateDirectory(tempDir);
        var pcm = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".pcm");
        try
        {
            var modelPath = await _models.EnsureAsync(model, Path.Combine(Plugin.Instance?.DataFolderPath ?? tempDir, "whisper-models"), cancellationToken).ConfigureAwait(false);
            var speech = await _speech.ExtractPcmAsync(video, audio, pcm, cancellationToken).ConfigureAwait(false);
            var windows = WhisperEngine.SpeechWindows(speech);
            if (windows.Count == 0)
            {
                return new WhisperOutcome(false, "keine Sprache in der Tonspur erkannt");
            }

            var threads = config.WhisperThreads > 0 ? config.WhisperThreads : Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var segments = await Task.Run(
                () => WhisperEngine.TranscribeAsync(modelPath, pcm, windows, sourceLanguage, translate, threads, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var cues = WhisperPostProcessor.ToCues(segments, speech);
            if (cues.Count < 3)
            {
                return new WhisperOutcome(false, "Whisper hat keinen verwertbaren Text erkannt");
            }

            var stem = SubtitleFileWriter.BuildStem(video, Lang.ToThreeLetter(language), false, FileTag);
            var path = await _writer.SaveAsync(video, stem, Srt.Write(cues), cancellationToken).ConfigureAwait(false);
            var message = FormattableString.Invariant(
                $"Whisper {model.Key}: {cues.Count} Untertitel aus Tonspur {audio.Language ?? "?"}{(translate ? " (übersetzt)" : string.Empty)} in {sw.Elapsed.TotalMinutes:F1} min");
            _logger.LogInformation("SubtitleForge: {Message} → {Path}", message, path);
            return new WhisperOutcome(true, message, path);
        }
        finally
        {
            try
            {
                File.Delete(pcm);
            }
            catch (IOException)
            {
            }

            _gate.Release();
        }
    }
}
