using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Release properties parsed from a scene/anime style file or release name.
/// Subtitles made for the same group + source almost always have identical timing.
/// </summary>
public sealed partial class ReleaseInfo
{
    private static readonly string[] Editions = { "extended", "directors", "dc", "unrated", "uncut", "theatrical", "remastered", "imax", "final" };

    private static readonly Dictionary<string, string> Services = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amzn"] = "amzn", ["amazon"] = "amzn", ["nf"] = "nf", ["netflix"] = "nf", ["dsnp"] = "dsnp", ["dsny"] = "dsnp",
        ["hmax"] = "hmax", ["max"] = "hmax", ["atvp"] = "atvp", ["cr"] = "cr", ["crunchyroll"] = "cr", ["hulu"] = "hulu",
        ["pcok"] = "pcok", ["pmtp"] = "pmtp", ["adn"] = "adn", ["hidive"] = "hidive", ["b-global"] = "bglobal", ["sky"] = "sky",
        ["joyn"] = "joyn", ["rtlp"] = "rtlp",
    };

    /// <summary>Gets the release group (lowercase).</summary>
    public string? Group { get; private init; }

    /// <summary>Gets the source: bluray, web, hdtv, dvd.</summary>
    public string? SourceType { get; private init; }

    /// <summary>Gets the streaming service tag (e.g. nf, amzn, cr).</summary>
    public string? Service { get; private init; }

    /// <summary>Gets the resolution (e.g. 1080p).</summary>
    public string? Resolution { get; private init; }

    /// <summary>Gets the edition tokens (extended, directors, ...).</summary>
    public IReadOnlyList<string> EditionTags { get; private init; } = Array.Empty<string>();

    /// <summary>
    /// Parses a release or file name.
    /// </summary>
    /// <param name="name">Name or path.</param>
    /// <returns>Parsed info.</returns>
    public static ReleaseInfo Parse(string name)
    {
        var n = Path.GetFileName(name);
        n = KnownExtensionRegex().Replace(n, string.Empty);

        string? group = null;
        var anime = AnimeGroupRegex().Match(n);
        if (anime.Success)
        {
            group = anime.Groups[1].Value;
        }
        else
        {
            var scene = SceneGroupRegex().Match(n);
            if (scene.Success)
            {
                group = scene.Groups[1].Value;
            }
            else
            {
                // Sonarr-style anime naming: "... [WEBDL-1080p][8bit][x264][SubsPlease]" → last plausible bracket token.
                group = BracketRegex().Matches(n).Select(m => m.Groups[1].Value.Trim())
                    .LastOrDefault(IsPlausibleBracketGroup);
            }
        }

        var tokens = TokenRegex().Split(n.ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        var joined = " " + string.Join(' ', tokens) + " ";

        string? source = null;
        if (tokens.Any(t => t is "bluray" or "bdrip" or "brrip" or "bd" or "bdremux" or "remux" or "bdmv") || joined.Contains(" blu ray ", StringComparison.Ordinal))
        {
            source = "bluray";
        }
        else if (tokens.Any(t => t is "webdl" or "webrip" or "web") || joined.Contains(" web dl ", StringComparison.Ordinal))
        {
            source = "web";
        }
        else if (tokens.Any(t => t is "hdtv" or "pdtv" or "tvrip"))
        {
            source = "hdtv";
        }
        else if (tokens.Any(t => t is "dvdrip" or "dvd" or "dvd9" or "dvd5"))
        {
            source = "dvd";
        }

        var service = tokens.Select(t => Services.TryGetValue(t, out var s) ? s : null).FirstOrDefault(s => s is not null);
        if (service is not null && source is null)
        {
            source = "web";
        }

        var resolution = tokens.FirstOrDefault(t => ResolutionRegex().IsMatch(t));
        if (resolution == "4k")
        {
            resolution = "2160p";
        }

        var editions = Editions.Where(e => tokens.Contains(e)).ToList();
        if (group is not null && (group.Equals(resolution, StringComparison.OrdinalIgnoreCase) || group.Length > 20))
        {
            group = null;
        }

        return new ReleaseInfo
        {
            Group = group?.ToLowerInvariant(),
            SourceType = source,
            Service = service,
            Resolution = resolution,
            EditionTags = editions,
        };
    }

    /// <summary>
    /// Extracts season/episode from a release name ("S01E05", "1x05"). Season packs ("S01") return no episode.
    /// </summary>
    /// <param name="name">Release name.</param>
    /// <returns>Season and episode.</returns>
    public static (int? Season, int? Episode) ParseEpisode(string name)
    {
        var m = SxxEyyRegex().Match(name);
        if (!m.Success)
        {
            m = NxNNRegex().Match(name);
        }

        return m.Success
            ? (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            : (null, null);
    }

    [GeneratedRegex(@"(?i)(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,3})(?![0-9])")]
    private static partial Regex SxxEyyRegex();

    [GeneratedRegex(@"(?<![0-9a-zA-Z])(\d{1,2})x(\d{2,3})(?![0-9])")]
    private static partial Regex NxNNRegex();

    private static readonly HashSet<string> NotAGroup = new(StringComparer.OrdinalIgnoreCase)
    {
        "x264", "x265", "h264", "h265", "hevc", "avc", "aac", "flac", "opus", "ac3", "eac3", "ddp", "dts", "truehd", "atmos",
        "8bit", "10bit", "hdr", "hdr10", "dv", "sdr", "multi", "dual", "dual audio", "multi-subs", "multiple subtitle",
        "eng", "ger", "jpn", "ja", "en", "de", "german", "english", "japanese", "uncensored", "proper", "repack", "v2", "v3",
    };

    private static bool IsPlausibleBracketGroup(string token)
    {
        if (token.Length < 3 || token.Length > 24 || NotAGroup.Contains(token) || CrcRegex().IsMatch(token)
            || QualityTokenRegex().IsMatch(token) || token.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        return char.IsLetter(token[0]);
    }

    [GeneratedRegex(@"\[([^\]]+)\]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"^[0-9A-Fa-f]{8}$")]
    private static partial Regex CrcRegex();

    [GeneratedRegex(@"(?i)(\d{3,4}[pi]|bluray|web|hdtv|dvd|remux|bd|aac|ddp|x26\d|\d\.\d)")]
    private static partial Regex QualityTokenRegex();

    [GeneratedRegex(@"\.(mkv|mp4|avi|m2ts|ts|mov|wmv|srt|ass|ssa|sub|zip|rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex KnownExtensionRegex();

    [GeneratedRegex(@"^\s*\[([^\]]+)\]")]
    private static partial Regex AnimeGroupRegex();

    [GeneratedRegex(@"-([A-Za-z0-9]+)(?:\[[^\]]*\])?\s*$")]
    private static partial Regex SceneGroupRegex();

    [GeneratedRegex(@"[\s._\-\[\]\(\)]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^(2160p|1080p|1080i|720p|576p|480p|4k)$")]
    private static partial Regex ResolutionRegex();
}
