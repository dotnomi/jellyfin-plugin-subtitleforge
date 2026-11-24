using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Builds queries from library items and searches all configured sources.
/// </summary>
public class SubtitleSearchService
{
    private readonly IReadOnlyList<ISubtitleSource> _sources;
    private readonly QuotaGuard _quota;
    private readonly ILogger<SubtitleSearchService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleSearchService"/> class.
    /// </summary>
    /// <param name="subDl">SubDL.</param>
    /// <param name="openSubtitles">OpenSubtitles.</param>
    /// <param name="quota">Quota guard.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="jimaku">Jimaku.</param>
    public SubtitleSearchService(SubDlSource subDl, OpenSubtitlesSource openSubtitles, QuotaGuard quota, ILogger<SubtitleSearchService> logger, JimakuSource jimaku)
    {
        _quota = quota;
        _sources = new ISubtitleSource[] { openSubtitles, subDl, jimaku };
        OpenSubtitles = openSubtitles;
        _logger = logger;
    }

    /// <summary>Gets the OpenSubtitles source (for quota checks).</summary>
    public OpenSubtitlesSource OpenSubtitles { get; }

    /// <summary>Gets all sources.</summary>
    public IReadOnlyList<ISubtitleSource> Sources => _sources;

    /// <summary>Gets the quota guard.</summary>
    public QuotaGuard Quota => _quota;

    /// <summary>
    /// Builds a query for a video item.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="languages">ISO 639-1 languages.</param>
    /// <returns>Query.</returns>
    public static SubtitleQuery BuildQuery(Video video, IReadOnlyList<string> languages)
    {
        var q = new SubtitleQuery
        {
            Languages = languages,
            FilePath = video.Path,
            Year = video.ProductionYear,
            ImdbId = video.GetProviderId(MetadataProvider.Imdb),
            TmdbId = video.GetProviderId(MetadataProvider.Tmdb),
            Title = video.Name,
        };

        if (video is Episode ep)
        {
            q.IsEpisode = true;
            q.Season = ep.ParentIndexNumber;
            q.Episode = ep.IndexNumber;
            var series = ep.Series;
            q.Title = series?.Name ?? ep.SeriesName ?? video.Name;
            q.SeriesImdbId = series?.GetProviderId(MetadataProvider.Imdb);
            q.SeriesTmdbId = series?.GetProviderId(MetadataProvider.Tmdb);
            q.Year = series?.ProductionYear ?? q.Year;
            q.TmdbId = null;
        }

        if (!string.IsNullOrEmpty(video.Path))
        {
            q.Release = ReleaseInfo.Parse(video.Path);
            try
            {
                q.MovieHash = OpenSubtitlesHash.Compute(video.Path);
            }
            catch (IOException)
            {
                q.MovieHash = null;
            }
        }

        return q;
    }

    /// <summary>
    /// Searches all configured sources and ranks the result.
    /// </summary>
    /// <param name="query">Query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Ranked candidates (best first).</returns>
    public Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken cancellationToken) =>
        SearchAsync(query, null, cancellationToken);

    /// <summary>
    /// Searches all configured sources and ranks the result, and tells which sources could not be asked.
    /// </summary>
    /// <param name="query">Query.</param>
    /// <param name="failedSources">Receives the names of sources that failed (timeout, server error, bad key).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Ranked candidates (best first).</returns>
    public async Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, ICollection<string>? failedSources, CancellationToken cancellationToken)
    {
        var all = new List<SubtitleCandidate>();
        foreach (var source in _sources.Where(s => s.IsConfigured && !_quota.IsPaused(s.Name)))
        {
            try
            {
                var found = await source.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                all.AddRange(found);
                _logger.LogDebug("SubtitleForge: {Source} returned {Count} candidates for {Title}", source.Name, found.Count, query.Title);
            }
            catch (QuotaExceededException ex)
            {
                _quota.Pause(source.Name, ex.ResetUtc);
                _logger.LogWarning("SubtitleForge: {Source}: {Message} – paused until {Until}", source.Name, ex.Message, _quota.PausedUntil(source.Name));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // A timeout of the source's HTTP client is a failed search, not a cancelled run.
                _logger.LogWarning("SubtitleForge: search on {Source} failed: {Message}", source.Name, ex.Message);
                failedSources?.Add(source.Name);
            }
        }

        var config = Plugin.Instance?.Configuration;
        return CandidateRanker.Rank(all, query, false, config is null || config.LearnSourceQuality ? SourceStats.Bonuses() : null);
    }

    /// <summary>
    /// Gets a source by name.
    /// </summary>
    /// <param name="name">Name.</param>
    /// <returns>Source.</returns>
    public ISubtitleSource GetSource(string name) =>
        _sources.First(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
