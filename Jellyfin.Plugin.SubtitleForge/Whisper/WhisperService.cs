using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Jellyfin.Plugin.SubtitleForge.Translation;
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
    private readonly TranslationService _translation;
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
    /// <param name="translation">Offline translation.</param>
    /// <param name="logger">Logger.</param>
    public WhisperService(WhisperModels models, SpeechAnalyzer speech, SubtitleFileWriter writer, IApplicationPaths appPaths, TranslationService translation, ILogger<WhisperService> logger)
    {
        _translation = translation;
        _models = models;
        _speech = speech;
        _writer = writer;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// How well Whisper understands a language (lower = better); used to pick the source track for translations.
    /// </summary>
    /// <param name="language">ISO 639-1.</param>
    /// <returns>Rank.</returns>
    public static int RecognitionRank(string language) => language switch
    {
        "en" => 0,
        "de" or "es" or "fr" or "it" or "pt" or "nl" => 1,
        "ru" or "pl" or "sv" or "da" or "no" or "fi" or "cs" or "tr" => 2,
        "ja" or "zh" or "ko" => 3,
        _ => 4,
    };

    /// <summary>
    /// Picks the audio track for a target language: a track in that language (default first); otherwise – when
    /// translation is enabled – the track Whisper recognises best that can be translated, preferring direct
    /// translations (e.g. German dub → English before Japanese → English).
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">ISO 639-1 target.</param>
    /// <param name="config">Config.</param>
    /// <returns>Track and its language, or null.</returns>
    public static (MediaStream Audio, string SourceLanguage)? PickAudio(Video video, string language, PluginConfiguration config)
    {
        var audio = video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).ToList();
        var same = audio.Where(a => Lang.Same(a.Language, language)).OrderByDescending(a => a.IsDefault).FirstOrDefault();
        if (same is not null)
        {
            return (same, language);
        }

        var best = audio
            .Select(a => (Audio: a, Lang: Lang.ToTwoLetter(a.Language)))
            .Where(x => x.Lang is not null && TranslationService.RouteCost(x.Lang!, language) is not null)
            .OrderBy(x => TranslationService.RouteCost(x.Lang!, language))
            .ThenBy(x => RecognitionRank(x.Lang!))
            .ThenByDescending(x => x.Audio.IsDefault)
            .FirstOrDefault();
        return best.Audio is null ? null : (best.Audio, best.Lang!);
    }

    /// <summary>
    /// Generates a subtitle: transcribes the audio track and, if its language differs, translates offline.
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
        string sourceLanguage;
        if (audioOverride is not null)
        {
            audio = audioOverride;
            var source = Lang.ToTwoLetter(audio.Language);
            if (source is null)
            {
                return new WhisperOutcome(false, $"Sprache der Tonspur {audio.Index} unbekannt");
            }

            if (source != language && !TranslationService.CanTranslate(source, language))
            {
                return new WhisperOutcome(false, $"keine Übersetzung {source} → {language} verfügbar (offline: de↔en, ja↔en, ja↔de über Englisch; mit DeepL-Key fast alle Sprachen)");
            }

            sourceLanguage = source;
        }
        else
        {
            var pick = PickAudio(video, language, config);
            if (pick is null)
            {
                return new WhisperOutcome(false, $"keine passende Tonspur für {language}");
            }

            (audio, sourceLanguage) = pick.Value;
        }

        var translate = sourceLanguage != language;
        var model = WhisperModels.Get(config.WhisperModel);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tempDir = Path.Combine(_appPaths.TempDirectory, "subtitleforge");
        Directory.CreateDirectory(tempDir);
        var pcm = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".pcm");
        try
        {
            var dataDir = Plugin.Instance?.DataFolderPath ?? tempDir;
            var modelPath = await _models.EnsureAsync(model, Path.Combine(dataDir, "whisper-models"), cancellationToken).ConfigureAwait(false);
            var speech = await _speech.ExtractPcmAsync(video, audio, pcm, cancellationToken).ConfigureAwait(false);
            var windows = WhisperEngine.SpeechWindows(speech);
            if (windows.Count == 0)
            {
                return new WhisperOutcome(false, "keine Sprache in der Tonspur erkannt");
            }

            var threads = config.WhisperThreads > 0 ? config.WhisperThreads : Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var usedEngine = string.Empty;
            var segments = await Task.Run(
                () => WhisperEngine.TranscribeAsync(modelPath, pcm, windows, sourceLanguage, false, threads, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (translate)
            {
                // Clean first (hallucinations, loops), then translate the remaining sentences.
                segments = segments.Select(s => s with { Text = WhisperPostProcessor.CutRepetitions(s.Text) }).ToList();
                var (texts, engine) = await _translation.TranslateAsync(
                    segments.Select(s => s.Text).ToList(), sourceLanguage, language, Path.Combine(dataDir, "translation-models"), threads, null, cancellationToken).ConfigureAwait(false);
                segments = segments.Zip(texts, (s, t) => s with { Text = t }).Where(s => s.Text.Length > 0).ToList();
                usedEngine = engine;
            }

            var cues = WhisperPostProcessor.ToCues(segments, speech);
            if (cues.Count < 3)
            {
                return new WhisperOutcome(false, "Whisper hat keinen verwertbaren Text erkannt");
            }

            var stem = SubtitleFileWriter.BuildStem(video, Lang.ToThreeLetter(language), false, translate ? FileTag + "-" + sourceLanguage : FileTag);
            var path = await _writer.SaveAsync(video, stem, Srt.Write(cues), cancellationToken).ConfigureAwait(false);
            var message = FormattableString.Invariant(
                $"Whisper {model.Key}: {cues.Count} Untertitel aus Tonspur {sourceLanguage}{(translate ? $", übersetzt nach {language} ({usedEngine})" : string.Empty)} in {sw.Elapsed.TotalMinutes:F1} min");
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
