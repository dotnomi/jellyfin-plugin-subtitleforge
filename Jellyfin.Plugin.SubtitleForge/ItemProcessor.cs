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

    /// <summary>State: a fitting subtitle may exist but the source's daily limit was reached; retried next run.</summary>
    public const string WaitingForQuota = "waiting-quota";

    private static readonly HashSet<string> AssCodecs = new(StringComparer.OrdinalIgnoreCase) { "ass", "ssa" };
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
    private readonly Throttle _throttle;
    private readonly Translation.SubtitleTranslator _translator;

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
    /// <param name="throttle">Throttle (pause while someone is watching).</param>
    /// <param name="translator">Translator for existing subtitles.</param>
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
        ILogger<ItemProcessor> logger,
        Throttle throttle,
        Translation.SubtitleTranslator translator)
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
        _throttle = throttle;
        _translator = translator;
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

        await _throttle.WaitForPlaybackAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var written = 0;
            if (steps.HasFlag(ProcessSteps.Ocr))
            {
                written += await OcrAsync(video, languages, config, cancellationToken).ConfigureAwait(false);
                if (config.ConvertAssToSrt || config.DeriveForcedSubtitles)
                {
                    written += await AssAsync(video, languages, config, cancellationToken).ConfigureAwait(false);
                }
            }

            if (steps.HasFlag(ProcessSteps.Download))
            {
                written += await DownloadAsync(video, languages, config, force, cancellationToken).ConfigureAwait(false);

                // No human-made subtitle found: translate an existing one of another language before Whisper.
                if (config.TranslateExistingSubtitles && config.TranslationEnabled)
                {
                    written += await TranslateMissingAsync(video, languages, force, cancellationToken).ConfigureAwait(false);
                }
            }

            // Automatic Whisper only where wanted: tag "whisper:auto" on the film/series, or the global switch.
            if (steps.HasFlag(ProcessSteps.Whisper) && config.WhisperEnabled && LanguageResolver.ResolveWhisperAuto(video, out _))
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
    /// <param name="audioIndex">Position of the audio track among the audio tracks (0 = first), or null for automatic.
    /// Not Jellyfin's stream index: that shifts whenever external subtitle files are added.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Outcome.</returns>
    /// <param name="replaceDownload">Remove SubtitleForge's own downloaded subtitle for this language afterwards and never
    /// download it again.</param>
    /// <param name="progress">Progress 0..1.</param>
    public async Task<WhisperOutcome> WhisperAsync(Video video, string language, int? audioIndex, bool replaceDownload, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        await _throttle.WaitForPlaybackAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tracks = video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal).OrderBy(s => s.Index).ToList();
            MediaStream? audio = null;
            if (audioIndex is { } position)
            {
                if (position < 0 || position >= tracks.Count)
                {
                    return new WhisperOutcome(false, $"Tonspur {position + 1} existiert nicht");
                }

                audio = tracks[position];
            }

            var outcome = await _whisper.GenerateAsync(video, language, audio, progress, cancellationToken).ConfigureAwait(false);
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

            // Forced either by container flag, or (checked after OCR) by the PGS forced flags. A stream without the
            // forced flag may have been saved as forced earlier – but only if no other stream of that language is a
            // flagged forced stream (whose file would otherwise be mistaken for this one).
            var ownStemExists = SubtitleFileWriter.Exists(video, SubtitleFileWriter.BuildStem(video, lang, s.IsForced, tag))
                || (sameKind.Count > 1 && !s.IsForced
                    && SubtitleFileWriter.Exists(video, SubtitleFileWriter.BuildStem(video, lang, true, OcrTask.FileTag))
                    && SubtitleFileWriter.Exists(video, SubtitleFileWriter.BuildStem(video, lang, false, OcrTask.FileTag)));
            var otherForcedStream = !s.IsForced && streams.Any(o => o != s && o.Type == MediaStreamType.Subtitle && !o.IsExternal
                && o.IsForced && Lang.Same(o.Language, s.Language));
            var savedAsForcedEarlier = !s.IsForced && !otherForcedStream
                && SubtitleFileWriter.Exists(video, SubtitleFileWriter.BuildStem(video, lang, true, tag));
            if (!ownStemExists && !savedAsForcedEarlier)
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
        var results = new List<(MediaStream Stream, string Stem, bool Forced, OcrResult Result, long Ms)>();
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

                results.Add((job.Stream, job.Stem, job.Forced || result.AllForced, result, sw.ElapsedMilliseconds));
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

        // Unflagged forced tracks: of several tracks in one language, one with only a small fraction of the cues
        // contains just signs/songs (e.g. Attack on Titan: 38 vs. 323 cues).
        var forcedByCount = new HashSet<MediaStream>(ForcedDetector.SmallTracks(
            results.Where(r => !r.Forced).Select(r => (r.Stream, Lang.ToTwoLetter(r.Stream.Language) ?? "und", r.Result.Cues.Count))));

        var written = 0;
        foreach (var r in results)
        {
            var forced = r.Forced || forcedByCount.Contains(r.Stream);
            var stem = r.Stem;
            if (forced && !r.Forced || forced && !stem.Contains(".forced.", StringComparison.Ordinal))
            {
                stem = SubtitleFileWriter.BuildStem(video, string.IsNullOrWhiteSpace(r.Stream.Language) ? "und" : r.Stream.Language, true, OcrTask.FileTag);
            }
            else if (!forced && forcedByCount.Count > 0 && results.Count(x => !x.Forced && !forcedByCount.Contains(x.Stream) && Lang.Same(x.Stream.Language, r.Stream.Language)) == 1)
            {
                // The remaining full track of that language no longer needs a number.
                stem = SubtitleFileWriter.BuildStem(video, string.IsNullOrWhiteSpace(r.Stream.Language) ? "und" : r.Stream.Language, false, OcrTask.FileTag);
            }

            var path = await _writer.SaveAsync(video, stem, Srt.Write(r.Result.Cues), cancellationToken).ConfigureAwait(false);
            written++;
            _logger.LogInformation(
                "SubtitleForge OCR: {Path} – {Cues} cues, mean confidence {Conf:P1}, {Low} low-confidence lines, {Ms} ms",
                path,
                r.Result.Cues.Count,
                r.Result.MeanConfidence,
                r.Result.LowConfidenceLines,
                r.Ms);
        }

        return written;
    }

    /// <summary>File name tag for subtitles converted from ASS/SSA.</summary>
    public const string AssTag = "Text";

    private async Task<int> AssAsync(Video video, List<string> languages, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var streams = video.GetMediaStreams();
        var written = 0;
        foreach (var s in streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && AssCodecs.Contains(s.Codec ?? string.Empty)))
        {
            var two = Lang.ToTwoLetter(s.Language);
            if (two is null || !languages.Contains(two))
            {
                continue;
            }

            var lang = s.Language!;
            var fullStem = SubtitleFileWriter.BuildStem(video, lang, s.IsForced, AssTag);
            var forcedStem = SubtitleFileWriter.BuildStem(video, lang, true, AssTag);
            var wantFull = config.ConvertAssToSrt && !SubtitleFileWriter.Exists(video, fullStem);

            // A forced track is only derived when none exists in that language (embedded, external or from OCR).
            var wantForced = config.DeriveForcedSubtitles && !s.IsForced && !SubtitleFileWriter.Exists(video, forcedStem)
                && !streams.Any(o => o.Type == MediaStreamType.Subtitle && o.IsForced && Lang.Same(o.Language, lang));
            if (!wantFull && !wantForced)
            {
                continue;
            }

            var tempDir = Path.Combine(_appPaths.TempDirectory, "subtitleforge");
            Directory.CreateDirectory(tempDir);
            var tmp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".ass");
            try
            {
                await _ffmpeg.ExtractStreamAsync(video.Path, StreamMap.Specifier(streams, s), "ass", tmp, cancellationToken).ConfigureAwait(false);
                var events = AssConverter.Parse(await File.ReadAllTextAsync(tmp, cancellationToken).ConfigureAwait(false));
                if (wantFull)
                {
                    var cues = AssConverter.Build(events, AssEventKind.Dialogue, AssEventKind.Song);
                    if (cues.Count >= 3)
                    {
                        await _writer.SaveAsync(video, fullStem, Srt.Write(cues), cancellationToken).ConfigureAwait(false);
                        written++;
                    }
                }

                if (wantForced)
                {
                    var cues = AssConverter.Build(events, AssEventKind.Sign, AssEventKind.Song);
                    var dialogue = events.Count(e => e.Kind == AssEventKind.Dialogue);
                    if (cues.Count >= 3 && dialogue > cues.Count)
                    {
                        await _writer.SaveAsync(video, forcedStem, Srt.Write(cues), cancellationToken).ConfigureAwait(false);
                        written++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleForge: ASS conversion failed for {Path} stream {Index}", video.Path, s.Index);
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
            if (!force && state is not null && state.Outcome != WaitingForQuota
                && state.LastTriedUtc > DateTime.UtcNow.AddDays(-Math.Max(1, config.DownloadRetryDays)))
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
        var forcedWanted = ForcedWanted(video, languages, config, force);
        if (missing.Count == 0 && forcedWanted.Count == 0)
        {
            return 0;
        }

        if (_search.Sources.Where(s => s.IsConfigured).All(s => _search.Quota.IsPaused(s.Name)))
        {
            foreach (var l in missing)
            {
                _state.Set(video.Id, l, WaitingForQuota, "Tageslimit aller Quellen erreicht – nächster Versuch beim nächsten Lauf");
            }

            return 0;
        }

        try
        {
            var query = SubtitleSearchService.BuildQuery(video, missing.Union(forcedWanted).ToList());
            var ranked = await _search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            var saved = 0;
            foreach (var lang in missing)
            {
                saved += await DownloadLanguageAsync(video, query, ranked, lang, config, cancellationToken).ConfigureAwait(false);
            }

            foreach (var lang in forcedWanted)
            {
                saved += await DownloadForcedAsync(video, query, ranked, lang, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Languages for which a forced subtitle (signs/foreign parts) should be downloaded: the video has an audio track
    /// in that language (dub), but no forced subtitle in it.
    /// </summary>
    private List<string> ForcedWanted(Video video, List<string> languages, PluginConfiguration config, bool force)
    {
        if (!config.DeriveForcedSubtitles)
        {
            return new List<string>();
        }

        var streams = video.GetMediaStreams();
        return languages.Where(l =>
                streams.Any(s => s.Type == MediaStreamType.Audio && Lang.Same(s.Language, l))
                && !streams.Any(s => s.Type == MediaStreamType.Subtitle && s.IsForced && Lang.Same(s.Language, l))
                && (force || _state.Get(video.Id, "forced:" + l) is not { } st || DateTime.UtcNow - st.LastTriedUtc > TimeSpan.FromDays(Math.Max(1, config.DownloadRetryDays))))
            .ToList();
    }

    private async Task<int> DownloadForcedAsync(Video video, SubtitleQuery query, List<SubtitleCandidate> ranked, string lang, CancellationToken cancellationToken)
    {
        // Forced subtitles cover too little speech to be verified against the audio, so only exact matches
        // (file hash or matching release) are taken.
        var candidate = ranked.FirstOrDefault(c => c.Language == lang && !c.MachineTranslated
            && (c.Forced || ForcedDetector.NameSaysForced(c.Releases))
            && (c.HashMatch || c.IsSafeMatch)
            && !_search.Quota.IsPaused(c.Source)
            && !_state.IsBlocked(video.Id, lang, c.Source + "|" + c.Reference));
        if (candidate is null)
        {
            _state.Set(video.Id, "forced:" + lang, "none-found", null);
            return 0;
        }

        var (data, fileName) = await _search.GetSource(candidate.Source).DownloadAsync(candidate, cancellationToken).ConfigureAwait(false);
        var cues = SubtitleContent.Parse(data, fileName, query.Season, query.Episode);
        if (cues is null || cues.Count == 0)
        {
            _state.Set(video.Id, "forced:" + lang, "error", "Datei nicht lesbar");
            return 0;
        }

        var stem = SubtitleFileWriter.BuildStem(video, Lang.ToThreeLetter(lang), true, candidate.Source);
        await _writer.SaveAsync(video, stem, Srt.Write(cues), cancellationToken).ConfigureAwait(false);
        _state.Set(video.Id, "forced:" + lang, "saved", $"{candidate.Source}: {candidate.DisplayName}", candidate.Source + "|" + candidate.Reference);
        return 1;
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
        if (full.Count == 0 && _search.Sources.Any(s => s.IsConfigured && _search.Quota.IsPaused(s.Name)))
        {
            // A paused source was not even asked – keep the item open for the next run.
            _state.Set(video.Id, lang, WaitingForQuota, "Nicht alle Quellen befragt (Tageslimit) – nächster Versuch beim nächsten Lauf");
            return 0;
        }

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
        var quotaBlocked = false;
        var attempts = verify ? Math.Max(1, config.SyncMaxCandidates) : MaxDownloadAttemptsPerLanguage;
        foreach (var candidate in usable.Take(attempts))
        {
            if (_search.Quota.IsPaused(candidate.Source))
            {
                quotaBlocked = true;
                continue;
            }

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
                _search.Quota.Pause(candidate.Source, ex.ResetUtc);
                quotaBlocked = true;
                _logger.LogWarning("SubtitleForge: {Message} – {Source} paused until {Until}", ex.Message, candidate.Source, _search.Quota.PausedUntil(candidate.Source));
            }
        }

        if (quotaBlocked)
        {
            // Not a failure: the item stays open and is retried on the next run (e.g. tomorrow night).
            _state.Set(video.Id, lang, WaitingForQuota, "Tageslimit erreicht – wird beim nächsten Lauf fortgesetzt");
            return 0;
        }

        _state.Set(
            video.Id,
            lang,
            rejected.Count > 0 ? "rejected-sync" : "download-failed",
            rejected.Count > 0 ? $"{rejected.Count} Kandidat(en) passten nicht zur Tonspur: {string.Join(" | ", rejected.Take(3))}" : null);
        return 0;
    }

    /// <summary>
    /// Manually translates an existing subtitle into a language.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">Target language.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <returns>Outcome.</returns>
    public async Task<WhisperOutcome> TranslateAsync(Video video, string language, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        await _throttle.WaitForPlaybackAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await _translator.TranslateAsync(video, language, progress, cancellationToken).ConfigureAwait(false);
            _state.Set(video.Id, "translate:" + language, outcome.Success ? "saved" : "failed", outcome.Message);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> TranslateMissingAsync(Video video, List<string> languages, bool force, CancellationToken cancellationToken)
    {
        var written = 0;
        foreach (var lang in languages)
        {
            if (HasLanguage(video.GetMediaStreams(), lang) || _state.Get(video.Id, lang)?.Outcome == "saved"
                || _state.Get(video.Id, lang)?.Outcome == WaitingForQuota)
            {
                continue; // present, just downloaded, or a source was not asked yet (quota) – try downloading again first
            }

            if (!force && _state.Get(video.Id, "translate:" + lang) is not null)
            {
                continue;
            }

            if (Translation.SubtitleTranslator.PickSource(video.GetMediaStreams(), lang) is null)
            {
                continue;
            }

            try
            {
                var outcome = await _translator.TranslateAsync(video, lang, null, cancellationToken).ConfigureAwait(false);
                _state.Set(video.Id, "translate:" + lang, outcome.Success ? "saved" : "failed", outcome.Message);
                written += outcome.Success ? 1 : 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleForge: translating a subtitle into {Lang} failed for {Path}", lang, video.Path);
                _state.Set(video.Id, "translate:" + lang, "failed", ex.Message);
            }
        }

        return written;
    }

    private async Task<int> WhisperFallbackAsync(Video video, List<string> languages, bool force, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var written = 0;
        foreach (var lang in languages)
        {
            // Streams are refreshed asynchronously after saving, so also check what this run just downloaded.
            if (HasLanguage(video.GetMediaStreams(), lang) || _state.Get(video.Id, lang)?.Outcome == "saved"
                || _state.Get(video.Id, "translate:" + lang)?.Outcome == "saved")
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
