using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Jellyfin.Plugin.SubtitleForge.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Scheduled task: downloads missing subtitles in the wanted languages, but only when the match is trustworthy.
/// </summary>
public class DownloadTask : IScheduledTask
{
    private const int MaxDownloadAttemptsPerLanguage = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleSearchService _search;
    private readonly SubtitleFileWriter _writer;
    private readonly DownloadStateStore _state;
    private readonly SyncService _sync;
    private readonly ILogger<DownloadTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="search">Search service.</param>
    /// <param name="writer">Writer.</param>
    /// <param name="state">State store.</param>
    /// <param name="sync">Sync service.</param>
    /// <param name="logger">Logger.</param>
    public DownloadTask(ILibraryManager libraryManager, SubtitleSearchService search, SubtitleFileWriter writer, DownloadStateStore state, SyncService sync, ILogger<DownloadTask> logger)
    {
        _sync = sync;
        _libraryManager = libraryManager;
        _search = search;
        _writer = writer;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Fehlende Untertitel herunterladen";

    /// <inheritdoc />
    public string Key => "SubtitleForgeDownload";

    /// <inheritdoc />
    public string Description => "Lädt fehlende Untertitel (SubDL, OpenSubtitles) in den gewünschten Sprachen, prüft sie gegen die Tonspur und korrigiert das Timing.";

    /// <inheritdoc />
    public string Category => "SubtitleForge";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!_search.Sources.Any(s => s.IsConfigured))
        {
            _logger.LogWarning("SubtitleForge download: no source configured (SubDL / OpenSubtitles API key missing)");
            progress.Report(100);
            return;
        }

        var languages = config.DownloadLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Lang.ToTwoLetter).Where(l => l is not null).Cast<string>().Distinct().ToList();

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Video>().Where(v => !string.IsNullOrEmpty(v.Path) && File.Exists(v.Path)).ToList();

        int saved = 0, skipped = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / items.Count);
            var video = items[i];
            var missing = MissingLanguages(video, languages, config);
            if (missing.Count == 0)
            {
                continue;
            }

            try
            {
                saved += await ProcessAsync(video, missing, config, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                skipped++;
                _logger.LogError(ex, "SubtitleForge download failed for {Path}", video.Path);
                foreach (var l in missing)
                {
                    _state.Set(video.Id, l, "error", ex.Message);
                }
            }

            // Be gentle with the APIs (OpenSubtitles: max 5 requests/second).
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("SubtitleForge download: {Saved} subtitle(s) saved, {Errors} error(s)", saved, skipped);
        progress.Report(100);
    }

    private List<string> MissingLanguages(Video video, List<string> languages, PluginConfiguration config)
    {
        var streams = video.GetMediaStreams();
        var result = new List<string>();
        foreach (var lang in languages)
        {
            var has = streams.Any(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && Lang.Same(s.Language, lang));
            if (has)
            {
                continue;
            }

            var state = _state.Get(video.Id, lang);
            if (state is not null && state.LastTriedUtc > DateTime.UtcNow.AddDays(-Math.Max(1, config.DownloadRetryDays)))
            {
                continue;
            }

            result.Add(lang);
        }

        return result;
    }

    private async Task<int> ProcessAsync(Video video, List<string> missing, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var query = SubtitleSearchService.BuildQuery(video, missing);
        var ranked = await _search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        var saved = 0;

        foreach (var lang in missing)
        {
            var candidates = ranked
                .Where(c => c.Language == lang && !c.Forced && !c.MachineTranslated)
                .Where(c => c.Source != _search.OpenSubtitles.Name || c.HashMatch || !config.OpenSubtitlesOnlyHashMatches)
                .ToList();

            if (candidates.Count == 0)
            {
                _state.Set(video.Id, lang, "none-found", null);
                _logger.LogInformation("SubtitleForge: no {Lang} subtitle found for {Name}", lang, video.Path);
                continue;
            }

            // Without audio verification only safe matches are allowed. With it, unsafe candidates are allowed too,
            // but every candidate (safe or not) must pass the speech check and gets its timing corrected.
            var verify = config.SyncEnabled;
            var usable = verify || !config.DownloadOnlySafeMatches
                ? candidates.OrderByDescending(c => c.IsSafeMatch).ThenByDescending(c => c.Score).ToList()
                : candidates.Where(c => c.IsSafeMatch).ToList();
            if (usable.Count == 0)
            {
                var best = candidates[0];
                _state.Set(video.Id, lang, "no-safe-match", $"{candidates.Count} candidates, best: {best.DisplayName} ({string.Join(", ", best.ScoreReasons)})");
                _logger.LogInformation(
                    "SubtitleForge: {Count} {Lang} candidate(s) for {Name}, but none matches the release (best: {Best}) – skipped to avoid out-of-sync subtitles",
                    candidates.Count,
                    lang,
                    Path.GetFileName(video.Path),
                    best.DisplayName);
                continue;
            }

            var done = false;
            var attempts = verify ? Math.Max(1, config.SyncMaxCandidates) : MaxDownloadAttemptsPerLanguage;
            foreach (var candidate in usable.Take(attempts))
            {
                try
                {
                    var (data, fileName) = await _search.GetSource(candidate.Source).DownloadAsync(candidate, cancellationToken).ConfigureAwait(false);
                    var cues = SubtitleContent.Parse(data, fileName, query.Season, query.Episode);
                    if (cues is null)
                    {
                        _logger.LogWarning("SubtitleForge: {Source} payload {File} contained no usable subtitle", candidate.Source, fileName);
                        continue;
                    }

                    var note = string.Join(", ", candidate.ScoreReasons);
                    if (verify)
                    {
                        var alignment = await _sync.AlignAsync(video, cues, false, cancellationToken).ConfigureAwait(false);
                        if (alignment is null)
                        {
                            // Audio could not be analysed: fall back to the release-match rule.
                            if (!candidate.IsSafeMatch && config.DownloadOnlySafeMatches)
                            {
                                continue;
                            }
                        }
                        else if (!alignment.IsConfident)
                        {
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
                        }
                    }

                    var stem = SubtitleFileWriter.BuildStem(video, Lang.ToThreeLetter(lang), false, candidate.Source);
                    var path = await _writer.SaveAsync(video, stem, Srt.Write(cues), cancellationToken).ConfigureAwait(false);
                    _state.Set(video.Id, lang, "saved", $"{candidate.Source}: {candidate.DisplayName} ({note})");
                    _logger.LogInformation("SubtitleForge: saved {Path} from {Source} [{Reasons}]", path, candidate.Source, note);
                    saved++;
                    done = true;
                    break;
                }
                catch (QuotaExceededException ex)
                {
                    _logger.LogWarning("SubtitleForge: {Message} (reset {Reset})", ex.Message, ex.ResetUtc);
                }
            }

            if (!done)
            {
                _state.Set(video.Id, lang, "download-failed", null);
            }
        }

        return saved;
    }
}
