using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// SubDL (https://subdl.com) via its official API. Free API key, generous daily limits, server-side unpacking of season packs.
/// </summary>
public class SubDlSource : ISubtitleSource
{
    private const string ApiBase = "https://api.subdl.com/api/v1/subtitles";
    private const string DownloadBase = "https://dl.subdl.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubDlSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubDlSource"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public SubDlSource(IHttpClientFactory httpClientFactory, ILogger<SubDlSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubDL";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    private static string? ApiKey => Plugin.Instance?.Configuration.SubDlApiKey;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken cancellationToken)
    {
        if (!IsConfigured || query.Languages.Count == 0)
        {
            return Array.Empty<SubtitleCandidate>();
        }

        // One request per language: results are capped at 30 per page, and a popular title's English
        // subtitles would otherwise crowd out every German one.
        var all = new List<SubtitleCandidate>();
        foreach (var lang in query.Languages)
        {
            all.AddRange(await SearchLanguageAsync(query, lang, cancellationToken).ConfigureAwait(false));
        }

        return all;
    }

    private async Task<IReadOnlyList<SubtitleCandidate>> SearchLanguageAsync(SubtitleQuery query, string language, CancellationToken cancellationToken)
    {
        var p = new List<(string, string)>
        {
            ("api_key", ApiKey!),
            ("languages", language.ToUpperInvariant()),
            ("releases", "1"),
            ("unpack", "1"),
            ("subs_per_page", "30"),
            ("type", query.IsEpisode ? "tv" : "movie"),
        };

        var imdb = query.IsEpisode ? query.SeriesImdbId : query.ImdbId;
        var tmdb = query.IsEpisode ? query.SeriesTmdbId : query.TmdbId;
        if (!string.IsNullOrEmpty(imdb))
        {
            p.Add(("imdb_id", imdb));
        }
        else if (!string.IsNullOrEmpty(tmdb))
        {
            p.Add(("tmdb_id", tmdb));
        }
        else
        {
            p.Add(("film_name", query.Title));
            if (query.Year.HasValue)
            {
                p.Add(("year", query.Year.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (query.IsEpisode)
        {
            if (query.Season.HasValue)
            {
                p.Add(("season_number", query.Season.Value.ToString(CultureInfo.InvariantCulture)));
            }

            if (query.Episode.HasValue)
            {
                p.Add(("episode_number", query.Episode.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var url = ApiBase + "?" + string.Join('&', p.Select(x => x.Item1 + "=" + Uri.EscapeDataString(x.Item2)));
        using var client = CreateClient();
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new QuotaExceededException("SubDL rate/daily limit reached", DateTime.UtcNow.Date.AddDays(1));
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            _logger.LogWarning("SubDL search failed: HTTP {Status} {Body}", (int)response.StatusCode, Truncate(body));
            return Array.Empty<SubtitleCandidate>();
        }

        return ParseSearch(body, query);
    }

    /// <inheritdoc />
    public async Task<(byte[] Data, string? FileName)> DownloadAsync(SubtitleCandidate candidate, CancellationToken cancellationToken)
    {
        var url = candidate.Reference.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? candidate.Reference : DownloadBase + candidate.Reference;
        url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api_key=" + Uri.EscapeDataString(ApiKey ?? string.Empty);
        using var client = CreateClient();
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new QuotaExceededException("SubDL download limit reached", DateTime.UtcNow.Date.AddDays(1));
        }

        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (data, System.IO.Path.GetFileName(new Uri(url).AbsolutePath));
    }

    /// <summary>
    /// Parses a search response (internal for tests).
    /// </summary>
    /// <param name="json">Response body.</param>
    /// <param name="query">Query.</param>
    /// <returns>Candidates.</returns>
    internal static List<SubtitleCandidate> ParseSearch(string json, SubtitleQuery query)
    {
        var result = new List<SubtitleCandidate>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("subtitles", out var subs) || subs.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var s in subs.EnumerateArray())
        {
            var lang = Lang.ToTwoLetter(Str(s, "language")) ?? Lang.ToTwoLetter(Str(s, "lang"));
            if (lang is null || !query.Languages.Contains(lang))
            {
                continue;
            }

            var releases = new List<string>();
            if (s.TryGetProperty("releases", out var rel) && rel.ValueKind == JsonValueKind.Array)
            {
                releases.AddRange(rel.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!));
            }

            var releaseName = Str(s, "release_name");
            if (!string.IsNullOrEmpty(releaseName) && !releases.Contains(releaseName))
            {
                releases.Insert(0, releaseName);
            }

            var c = new SubtitleCandidate
            {
                Source = "SubDL",
                Reference = StripApiKey(Str(s, "url")),
                Language = lang,
                Releases = releases,
                HearingImpaired = Bool(s, "hi"),
                Author = Str(s, "author"),
                Season = Int(s, "season"),
                Episode = Int(s, "episode"),
                MachineTranslated = Bool(s, "ai_translated"),
            };

            if (query.IsEpisode)
            {
                var from = Int(s, "episode_from");
                var to = Int(s, "episode_end");
                var fullSeason = Bool(s, "full_season");
                if (fullSeason || (from.HasValue && to.HasValue && from != to))
                {
                    c.IsPack = true;
                    if (from.HasValue && to.HasValue && from != to && query.Episode.HasValue && (query.Episode < from || query.Episode > to))
                    {
                        continue;
                    }

                    // Prefer the per-episode file the server already extracted.
                    if (s.TryGetProperty("unpack_files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
                    {
                        foreach (var f in files.EnumerateArray())
                        {
                            if (Int(f, "episode") == query.Episode && (Int(f, "season") is null or 0 || Int(f, "season") == query.Season))
                            {
                                c.Reference = Str(f, "url") is { } u ? StripApiKey(u) : c.Reference;
                                c.Episode = query.Episode;
                                c.IsPack = false;
                                break;
                            }
                        }

                        // The server listed the pack's content and the wanted episode is not in it
                        // (typical for anime with absolute numbering, e.g. "episode 5–33").
                        if (c.IsPack)
                        {
                            continue;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(c.Reference))
            {
                result.Add(c);
            }
        }

        return result;
    }

    /// <summary>
    /// Download URLs returned by the API carry the API key; never keep it in references (they reach clients and logs).
    /// </summary>
    private static string StripApiKey(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return url;
        }

        var kept = url[(q + 1)..].Split('&').Where(p => !p.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase)).ToArray();
        return kept.Length == 0 ? url[..q] : url[..q] + "?" + string.Join('&', kept);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int? Int(JsonElement e, string name)
    {
        var s = Str(e, name);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0));

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(SubDlSource));
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SubtitleForge/0.1");
        return client;
    }
}
