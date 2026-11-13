using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Jellyfin.Plugin.SubtitleForge.Ocr;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Jellyfin.Plugin.SubtitleForge.Whisper;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge;

/// <summary>
/// What to do for an item.
/// </summary>
[Flags]
public enum ProcessSteps
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Convert embedded image subtitles.</summary>
    Ocr = 1,

    /// <summary>Download missing languages.</summary>
    Download = 2,

    /// <summary>Generate missing languages with Whisper (fallback).</summary>
    Whisper = 4,

    /// <summary>Everything.</summary>
    All = Ocr | Download | Whisper,
}

/// <summary>
/// Per-item pipeline used by the scheduled tasks, the new-item queue and manual actions:
/// OCR of embedded image subtitles → download of missing languages (verified against the audio) → Whisper fallback.
/// Only one item is processed at a time (NAS-friendly).
/// </summary>
public sealed class ItemProcessor : IDisposable
{
    private const int MaxDownloadAttemptsPerLanguage = 3;

    private static readonly HashSet<string> PgsCodecs = new(StringComparer.OrdinalIgnoreCase) { "pgssub", "hdmv_pgs_subtitle", "pgs", "sup" };
    private static readonly HashSet<string> ImageCodecs = new(StringComparer.OrdinalIgnoreCase) { "pgssub", "hdmv_pgs_subtitle", "pgs", "sup", "dvdsub", "dvd_subtitle", "vobsub", "dvbsub", "dvb_subtitle", "xsub" };

    private readonly LanguageResolver _languages;
    private readonly FfmpegRunner _ffmpeg;
    private readonly SubtitleFileWriter _writer;
    private readonly SubtitleSearchService _search;
    private readonly SyncService _sync;
    private readonly SpeechAnalyzer _speech;
    private readonly WhisperService _whisper;
    private readonly DownloadStateStore _state;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<ItemProcessor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemProcessor"/> class.
    /// </summary>
    /// <param name="languages">Language resolver.</param>
    /// <param name="ffmpeg">ffmpeg.</param>
    /// <param name="writer">Writer.</param>
    /// <param name="search">Search.</param>
    /// <param name="sync">Sync.</param>
    /// <param name="speech">Speech analyzer.</param>
    /// <param name="whisper">Whisper.</param>
    /// <param name="state">State.</param>
    /// <param name="appPaths">App paths.</param>
    /// <param name="logger">Logger.</param>
    public ItemProcessor(
        LanguageResolver languages,
        FfmpegRunner ffmpeg,
        SubtitleFileWriter writer,
        SubtitleSearchService search,
        SyncService sync,
        SpeechAnalyzer speech,
        WhisperService whisper,
        DownloadStateStore state,
        IApplicationPaths appPaths,
        ILogger<ItemProcessor> logger)
    {
        _languages = languages;
        _ffmpeg = ffmpeg;
        _writer = writer;
        _search = search;
        _sync = sync;
        _speech = speech;
        _whisper = whisper;
        _state = state;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Whether a non-forced subtitle in this language exists (embedded text/image or external).
    /// </summary>
    /// <param name="streams">Streams.</param>
    /// <param name="language">ISO 639-1.</param>
    /// <returns>True if present.</returns>
    public static bool HasLanguage(IEnumerable<MediaStream> streams, string language) =>
        streams.Any(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && Lang.Same(s.Language, language));

    /// <summary>
    /// Runs the pipeline for one item.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="steps">Steps.</param>
    /// <param name="force">Ignore the retry delay of earlier unsuccessful attempts.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Number of subtitles written.</returns>
    public async Task<int> ProcessAsync(Video video, ProcessSteps steps, bool force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(video.Path) || !File.Exists(video.Path))
        {
            return 0;
        }

        var languages = _languages.Resolve(video, out _);
        if (languages.Count == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var written = 0;
            if (steps.HasFlag(ProcessSteps.Ocr))
            {
                written += await OcrAsync(video, languages, config, cancellationToken).ConfigureAwait(false);
            }

            if (steps.HasFlag(ProcessSteps.Download))
            {
                written += await DownloadAsync(video, languages, config, force, cancellationToken).ConfigureAwait(false);
            }

            if (steps.HasFlag(ProcessSteps.Whisper) && config.WhisperEnabled && config.WhisperAutoFallback)
            {
                written += await WhisperFallbackAsync(video, languages, force, cancellationToken).ConfigureAwait(false);
            }

            return written;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Manually generates a subtitle with Whisper (optionally from a specific audio track).
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">Target language.</param>
    /// <param name="audioIndex">Jellyfin index of the audio stream, or null for automatic.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Outcome.</returns>
    /// <param name="replaceDownload">Remove SubtitleForge's own downloaded subtitle for this language afterwards and never
    /// download it again.</param>
    public async Task<WhisperOutcome> WhisperAsync(Video video, string language, int? audioIndex, bool replaceDownload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var audio = audioIndex is null ? null : video.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == audioIndex);
            var outcome = await _whisper.GenerateAsync(video, language, audio, null, cancellationToken).ConfigureAwait(false);
            _state.Set(video.Id, "whisper:" + language, outcome.Success ? "whisper" : "whisper-failed", outcome.Message);
            if (outcome.Success && replaceDownload)
            {
                var removed = _writer.DeleteOwnDownloads(video, Lang.ToThreeLetter(language), _search.Sources.Select(x => x.Name));
                var saved = _state.Get(video.Id, language);
                if (saved?.Reference is { } reference)
                {
                    _state.Block(video.Id, language, reference);
                }

                if (removed > 0)
                {
                    outcome = outcome with { Message = outcome.Message + $"; {removed} heruntergeladene Datei(en) ersetzt" };
                }
            }

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<int> OcrAsync(Video video, List<string> languages, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var streams = video.GetMediaStreams();
        var jobs = new List<(MediaStream Stream, string Stem, bool Forced)>();
        foreach (var s in streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && PgsCodecs.Contains(s.Codec ?? string.Empty)))
        {
            var two = Lang.ToTwoLetter(s.Language);
            if (two is null ? !config.OcrIncludeUndefined : !languages.Contains(two))
            {
                continue;
            }

            if (config.OcrSkipIfTextExists && streams.Any(o =>
                    o.Type == MediaStreamType.Subtitle && !o.IsExternal && !ImageCodecs.Contains(o.Codec ?? string.Empty)
                    && Lang.Same(o.Language, s.Language) && o.IsForced == s.IsForced))
            {
                continue;
            }

            var tag = OcrTask.FileTag;
            var sameKind = streams.Where(o => o.Type == MediaStreamType.Subtitle && !o.IsExternal && PgsCodecs.Contains(o.Codec ?? string.Empty)
                && string.Equals(o.Language, s.Language, StringComparison.OrdinalIgnoreCase) && o.IsForced == s.IsForced).ToList();
            if (sameKind.Count > 1)
            {
                tag += (sameKind.IndexOf(s) + 1).ToString(CultureInfo.InvariantCulture);
            }

            var lang = string.IsNullOrWhiteSpace(s.Language) ? "und" : s.Language;

            // Forced either by container flag, or (checked after OCR) by the PGS forced flags.
            if (!SubtitleFileWriter.Exists(video, SubtitleFileWriter.BuildStem(video, lang, true, tag))
                && !SubtitleFileWriter.Exists(video, SubtitleFileWriter.BuildStem(video, lang, false, tag)))
            {
                jobs.Add((s, SubtitleFileWriter.BuildStem(video, lang, s.IsForced, tag), s.IsForced));
            }
        }

        if (jobs.Count == 0)
        {
            return 0;
        }

        var tempDir = Path.Combine(_appPaths.TempDirectory, "subtitleforge");
        Directory.CreateDirectory(tempDir);
        using var recognizer = new PaddleRecognizer(config.OcrThreads);
        var written = 0;
        foreach (var job in jobs)
        {
            var sup = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".sup");
            try
            {
                var sw = Stopwatch.StartNew();
                await _ffmpeg.ExtractStreamAsync(video.Path, StreamMap.Specifier(streams, job.Stream), "sup", sup, cancellationToken).ConfigureAwait(false);
                OcrResult result;
                await using (var fs = File.OpenRead(sup))
                {
                    result = ImageSubtitleOcr.Convert(fs, recognizer, null, cancellationToken);
                }

                if (result.Cues.Count == 0)
                {
                    _logger.LogWarning("SubtitleForge OCR: no text recognized in {Path} stream {Index}", video.Path, job.Stream.Index);
                    continue;
                }

                var stem = job.Stem;
                if (!job.Forced && result.AllForced)
                {
                    stem = SubtitleFileWriter.BuildStem(video, string.IsNullOrWhiteSpace(job.Stream.Language) ? "und" : job.Stream.Language, true, Path.GetExtension(job.Stem).TrimStart('.'));
                }

                var path = await _writer.SaveAsync(video, stem, Srt.Write(result.Cues), cancellationToken).ConfigureAwait(false);
                written++;
                _logger.LogInformation(
                    "SubtitleForge OCR: {Path} – {Cues} cues, mean confidence {Conf:P1}, {Low} low-confidence lines, {Ms} ms",
                    path,
                    result.Cues.Count,
                    result.MeanConfidence,
                    result.LowConfidenceLines,
                    sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleForge OCR failed for {Path} stream {Index}", video.Path, job.Stream.Index);
            }
            finally
            {
                try
                {
                    File.Delete(sup);
                }
                catch (IOException)
                {
                }
            }
        }

        return written;
    }

    private List<string> MissingLanguages(Video video, List<string> languages, PluginConfiguration config, bool force, string statePrefix)
    {
        var streams = video.GetMediaStreams();
        var result = new List<string>();
        foreach (var lang in languages)
        {
            if (HasLanguage(streams, lang))
            {
                continue;
            }

            var state = _state.Get(video.Id, statePrefix + lang);
            if (!force && state is not null && state.LastTriedUtc > DateTime.UtcNow.AddDays(-Math.Max(1, config.DownloadRetryDays)))
            {
                continue;
            }

            result.Add(lang);
        }

        return result;
    }

    private async Task<int> DownloadAsync(Video video, List<string> languages, PluginConfiguration config, bool force, CancellationToken cancellationToken)
    {
        if (!_search.Sources.Any(s => s.IsConfigured))
        {
            return 0;
        }

        var missing = MissingLanguages(video, languages, config, force, string.Empty);
        if (missing.Count == 0)
        {
            return 0;
        }

        try
        {
            var query = SubtitleSearchService.BuildQuery(video, missing);
            var ranked = await _search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var saved = 0;
            foreach (var lang in missing)
            {
                saved += await DownloadLanguageAsync(video, query, ranked, lang, config, cancellationToken).ConfigureAwait(false);
            }

            // Be gentle with the APIs (OpenSubtitles: max 5 requests/second).
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            return saved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubtitleForge download failed for {Path}", video.Path);
            foreach (var l in missing)
            {
                _state.Set(video.Id, l, "error", ex.Message);
            }

            return 0;
        }
    }

    private async Task<int> DownloadLanguageAsync(Video video, SubtitleQuery query, List<SubtitleCandidate> ranked, string lang, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var candidates = ranked
            .Where(c => c.Language == lang && !c.MachineTranslated)
            .Where(c => c.Source != _search.OpenSubtitles.Name || c.HashMatch || !config.OpenSubtitlesOnlyHashMatches)
            .Where(c => !_state.IsBlocked(video.Id, lang, c.Source + "|" + c.Reference))
            .ToList();
        foreach (var c in candidates)
        {
            c.Forced |= ForcedDetector.NameSaysForced(c.Releases);
        }

        // Full subtitles first; forced ones are only interesting if the file has none in this language.
        var full = candidates.Where(c => !c.Forced).ToList();
        if (full.Count == 0)
        {
            _state.Set(video.Id, lang, "none-found", candidates.Count > 0 ? $"nur {candidates.Count} Forced-Untertitel gefunden" : null);
            _logger.LogInformation("SubtitleForge: no {Lang} subtitle found for {Name}", lang, video.Path);
            return 0;
        }

        var verify = config.SyncEnabled;
        var usable = verify || !config.DownloadOnlySafeMatches
            ? full.OrderByDescending(c => c.IsSafeMatch).ThenByDescending(c => c.Score).ToList()
            : full.Where(c => c.IsSafeMatch).ToList();
        if (usable.Count == 0)
        {
            var best = full[0];
            _state.Set(video.Id, lang, "no-safe-match", $"{full.Count} Kandidaten, bester: {best.DisplayName} ({string.Join(", ", best.ScoreReasons)})");
            return 0;
        }

        var rejected = new List<string>();
        var attempts = verify ? Math.Max(1, config.SyncMaxCandidates) : MaxDownloadAttemptsPerLanguage;
        foreach (var candidate in usable.Take(attempts))
        {
            try
            {
                var (data, fileName) = await _search.GetSource(candidate.Source).DownloadAsync(candidate, cancellationToken).ConfigureAwait(false);
                var cues = SubtitleContent.Parse(data, fileName, query.Season, query.Episode);
                if (cues is null)
                {
                    continue;
                }

                var note = string.Join(", ", candidate.ScoreReasons);
                SpeechSignal? speech = null;
                if (verify)
                {
                    var alignment = await _sync.AlignAsync(video, cues, false, cancellationToken).ConfigureAwait(false);
                    if (alignment is null)
                    {
                        if (!candidate.IsSafeMatch && config.DownloadOnlySafeMatches)
                        {
                            continue;
                        }
                    }
                    else if (!alignment.IsConfident)
                    {
                        rejected.Add(candidate.DisplayName);
                        _logger.LogInformation(
                            "SubtitleForge: rejected {Source} {Release} for {Name}: does not match the audio ({Alignment})",
                            candidate.Source,
                            candidate.DisplayName,
                            Path.GetFileName(video.Path),
                            alignment);
                        continue;
                    }
                    else
                    {
                        cues = alignment.Cues;
                        note += "; sync: " + alignment;
                        speech = _speech.TryGetCached(video);
                    }
                }

                // Sources rarely flag forced subtitles: compare against the amount of dialogue.
                var runtimeMs = (video.RunTimeTicks ?? 0) / TimeSpan.TicksPerMillisecond;
                var forced = ForcedDetector.IsLikelyForced(cues, speech, runtimeMs);
                if (forced)
                {
                    _logger.LogInformation("SubtitleForge: {Release} is a forced subtitle (covers little dialogue)", candidate.DisplayName);
                    var hasForced = video.GetMediaStreams().Any(s => s.Type == MediaStreamType.Subtitle && s.IsForced && Lang.Same(s.Language, lang));
                    if (!hasForced)
                    {
                        await _writer.SaveAsync(video, SubtitleFileWriter.BuildStem(video, Lang.ToThreeLetter(lang), true, candidate.Source), Srt.Write(cues), cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var stem = SubtitleFileWriter.BuildStem(video, Lang.ToThreeLetter(lang), false, candidate.Source);
                var path = await _writer.SaveAsync(video, stem, Srt.Write(cues), cancellationToken).ConfigureAwait(false);
                _state.Set(video.Id, lang, "saved", $"{candidate.Source}: {candidate.DisplayName} ({note})", candidate.Source + "|" + candidate.Reference);
                _logger.LogInformation("SubtitleForge: saved {Path} from {Source} [{Reasons}]", path, candidate.Source, note);
                return 1;
            }
            catch (QuotaExceededException ex)
            {
                _logger.LogWarning("SubtitleForge: {Message} (reset {Reset})", ex.Message, ex.ResetUtc);
            }
        }

        _state.Set(
            video.Id,
            lang,
            rejected.Count > 0 ? "rejected-sync" : "download-failed",
            rejected.Count > 0 ? $"{rejected.Count} Kandidat(en) passten nicht zur Tonspur: {string.Join(" | ", rejected.Take(3))}" : null);
        return 0;
    }

    private async Task<int> WhisperFallbackAsync(Video video, List<string> languages, bool force, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var written = 0;
        foreach (var lang in languages)
        {
            // Streams are refreshed asynchronously after saving, so also check what this run just downloaded.
            if (HasLanguage(video.GetMediaStreams(), lang) || _state.Get(video.Id, lang)?.Outcome == "saved")
            {
                continue;
            }

            // Whisper is expensive: one automatic attempt per item and language (unless forced manually).
            if (!force && _state.Get(video.Id, "whisper:" + lang) is not null)
            {
                continue;
            }

            if (WhisperService.PickAudio(video, lang, config) is null)
            {
                continue;
            }

            var outcome = await _whisper.GenerateAsync(video, lang, null, null, cancellationToken).ConfigureAwait(false);
            _state.Set(video.Id, "whisper:" + lang, outcome.Success ? "whisper" : "whisper-failed", outcome.Message);
            if (outcome.Success)
            {
                written++;
            }
        }

        return written;
    }
}
