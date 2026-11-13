using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// What to search for.
/// </summary>
public sealed class SubtitleQuery
{
    /// <summary>Gets or sets a value indicating whether this is a TV episode.</summary>
    public bool IsEpisode { get; set; }

    /// <summary>Gets or sets the movie title or series name.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets the IMDb id of the movie or episode.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the TMDb id of the movie.</summary>
    public string? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id of the series.</summary>
    public string? SeriesImdbId { get; set; }

    /// <summary>Gets or sets the TMDb id of the series.</summary>
    public string? SeriesTmdbId { get; set; }

    /// <summary>Gets or sets the wanted languages (ISO 639-1).</summary>
    public IReadOnlyList<string> Languages { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the media file path.</summary>
    public string? FilePath { get; set; }

    /// <summary>Gets or sets the OpenSubtitles movie hash of the file.</summary>
    public string? MovieHash { get; set; }

    /// <summary>Gets or sets the parsed release info of the file.</summary>
    public ReleaseInfo? Release { get; set; }
}

/// <summary>
/// A subtitle offered by a source.
/// </summary>
public sealed class SubtitleCandidate
{
    /// <summary>Gets or sets the source name.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the source-specific download reference.</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>Gets or sets the language (ISO 639-1).</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the release names the subtitle was made for.</summary>
    public List<string> Releases { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the file hash matched (exact same video file).</summary>
    public bool HashMatch { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a hearing impaired subtitle.</summary>
    public bool HearingImpaired { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a forced (foreign parts only) subtitle.</summary>
    public bool Forced { get; set; }

    /// <summary>Gets or sets a value indicating whether this was machine/AI translated.</summary>
    public bool MachineTranslated { get; set; }

    /// <summary>Gets or sets the season (episodes only).</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode (episodes only).</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets a value indicating whether the reference is a multi-episode pack.</summary>
    public bool IsPack { get; set; }

    /// <summary>Gets or sets the download count.</summary>
    public int? Downloads { get; set; }

    /// <summary>Gets or sets the author/uploader.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets the computed score (higher is better).</summary>
    public int Score { get; set; }

    /// <summary>Gets or sets the reasons that made up the score (for logs/UI).</summary>
    public List<string> ScoreReasons { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the match is considered safe without audio sync verification.</summary>
    public bool IsSafeMatch { get; set; }

    /// <summary>
    /// Gets a display name.
    /// </summary>
    public string DisplayName => Releases.Count > 0 ? Releases[0] : Source + " (ohne Release-Namen)";
}

/// <summary>
/// Thrown when a source's quota is exhausted.
/// </summary>
public class QuotaExceededException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="QuotaExceededException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="resetUtc">When the quota resets.</param>
    public QuotaExceededException(string message, DateTime? resetUtc)
        : base(message)
    {
        ResetUtc = resetUtc;
    }

    /// <summary>Gets the reset time.</summary>
    public DateTime? ResetUtc { get; }
}
