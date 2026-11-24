using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// OpenSubtitles.com REST API. Searching is free; downloads are limited per day, so the download job only spends
/// them on hash matches (exact same file → guaranteed timing). Quota state is tracked from the API responses.
/// </summary>
public sealed class OpenSubtitlesSource : ISubtitleSource, IDisposable
{
    private const string UserAgent = "SubtitleForge v0.1";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenSubtitlesSource> _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string _apiHost = "api.opensubtitles.com";
    private string? _token;
    private DateTime _tokenExpiresUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesSource"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public OpenSubtitlesSource(IHttpClientFactory httpClientFactory, ILogger<OpenSubtitlesSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "OpenSubtitles";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.OpenSubtitlesApiKey);

    /// <summary>Gets the remaining downloads reported by the last download (null = unknown).</summary>
    public int? RemainingDownloads { get; private set; }

    /// <summary>Gets when the download quota resets (UTC).</summary>
    public DateTime? QuotaResetUtc { get; private set; }

    /// <summary>
    /// Gets a value indicating whether downloads are currently blocked by the daily quota.
    /// </summary>
    public bool IsQuotaExhausted => RemainingDownloads <= 0 && QuotaResetUtc > DateTime.UtcNow;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken cancellationToken)
    {
        if (!IsConfigured || query.Languages.Count == 0)
        {
            return Array.Empty<SubtitleCandidate>();
        }

        // Parameters must be sorted alphabetically, otherwise the API answers with a redirect.
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ai_translated"] = "exclude",
            ["languages"] = string.Join(',', query.Languages.Select(l => l.ToLowerInvariant()).OrderBy(l => l, StringComparer.Ordinal)),
        };
        if (!string.IsNullOrEmpty(query.MovieHash))
        {
            p["moviehash"] = query.MovieHash;
        }

        if (query.IsEpisode)
        {
            if (!string.IsNullOrEmpty(query.ImdbId))
            {
                p["imdb_id"] = StripTt(query.ImdbId);
            }
            else if (!string.IsNullOrEmpty(query.SeriesImdbId))
            {
                p["parent_imdb_id"] = StripTt(query.SeriesImdbId);
            }
            else if (!string.IsNullOrEmpty(query.SeriesTmdbId))
            {
                p["parent_tmdb_id"] = query.SeriesTmdbId;
            }
            else
            {
                p["query"] = query.Title.ToLowerInvariant();
            }

            if (string.IsNullOrEmpty(query.ImdbId))
            {
                if (query.Season.HasValue)
                {
                    p["season_number"] = query.Season.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (query.Episode.HasValue)
                {
                    p["episode_number"] = query.Episode.Value.ToString(CultureInfo.InvariantCulture);
                }
            }
        }
        else if (!string.IsNullOrEmpty(query.ImdbId))
        {
            p["imdb_id"] = StripTt(query.ImdbId);
        }
        else if (!string.IsNullOrEmpty(query.TmdbId))
        {
            p["tmdb_id"] = query.TmdbId;
        }
        else
        {
            p["query"] = query.Title.ToLowerInvariant();
            if (query.Year.HasValue)
            {
                p["year"] = query.Year.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        var url = $"https://{_apiHost}/api/v1/subtitles?" + string.Join('&', p.Select(x => x.Key + "=" + Uri.EscapeDataString(x.Value)));
        using var client = CreateClient();
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new QuotaExceededException("OpenSubtitles rate limit reached", DateTime.UtcNow.AddMinutes(1));
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenSubtitles search failed: HTTP {Status} {Body}", (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
            return Array.Empty<SubtitleCandidate>();
        }

        return ParseSearch(body);
    }

    /// <inheritdoc />
    public async Task<(byte[] Data, string? FileName)> DownloadAsync(SubtitleCandidate candidate, CancellationToken cancellationToken)
    {
        if (IsQuotaExhausted)
        {
            throw new QuotaExceededException("OpenSubtitles daily download quota exhausted", QuotaResetUtc);
        }

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_apiHost}/api/v1/download")
        {
            Content = JsonContent.Create(new { file_id = long.Parse(candidate.Reference, CultureInfo.InvariantCulture), sub_format = "srt" }),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        UpdateQuota(body);
        if ((int)response.StatusCode == 406 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            RemainingDownloads = 0;
            throw new QuotaExceededException("OpenSubtitles daily download quota exhausted", QuotaResetUtc);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _token = null;
        }

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        var link = doc.RootElement.GetProperty("link").GetString() ?? throw new InvalidOperationException("No download link");
        var fileName = doc.RootElement.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
        // The file link is fetched without the API key and token (plain client), and only from OpenSubtitles hosts.
        var uri = SafeHttp.RequireHost(link, "opensubtitles.com", "opensubtitles.org");
        using var plain = _httpClientFactory.CreateClient(nameof(OpenSubtitlesSource) + ".Files");
        plain.Timeout = TimeSpan.FromSeconds(30);
        plain.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        var data = await SafeHttp.GetBytesLimitedAsync(plain, uri, SafeHttp.MaxSubtitleDownloadBytes, null, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("OpenSubtitles: downloaded {File}, {Remaining} downloads left today", fileName, RemainingDownloads);
        return (data, fileName);
    }

    /// <summary>
    /// Parses a search response (internal for tests).
    /// </summary>
    /// <param name="json">Body.</param>
    /// <returns>Candidates.</returns>
    internal static List<SubtitleCandidate> ParseSearch(string json)
    {
        var result = new List<SubtitleCandidate>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var a) || !a.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
            {
                continue;
            }

            var lang = Lang.ToTwoLetter(a.TryGetProperty("language", out var l) ? l.GetString() : null);
            if (lang is null)
            {
                continue;
            }

            var fileId = files[0].GetProperty("file_id").GetRawText();
            var release = a.TryGetProperty("release", out var r) ? r.GetString() : null;
            int? season = null, episode = null;
            if (a.TryGetProperty("feature_details", out var fd))
            {
                season = fd.TryGetProperty("season_number", out var sn) && sn.ValueKind == JsonValueKind.Number ? sn.GetInt32() : null;
                episode = fd.TryGetProperty("episode_number", out var en) && en.ValueKind == JsonValueKind.Number ? en.GetInt32() : null;
            }

            var hi = a.TryGetProperty("hearing_impaired", out var h) && h.ValueKind == JsonValueKind.True;
            var foreignOnly = a.TryGetProperty("foreign_parts_only", out var fpo) && fpo.ValueKind == JsonValueKind.True;
            result.Add(new SubtitleCandidate
            {
                Source = "OpenSubtitles",
                Reference = fileId,
                Language = lang,
                Releases = string.IsNullOrEmpty(release) ? new List<string>() : new List<string> { release },
                HashMatch = a.TryGetProperty("moviehash_match", out var mh) && mh.ValueKind == JsonValueKind.True,
                HearingImpaired = hi,
                Forced = foreignOnly && !hi,
                MachineTranslated = (a.TryGetProperty("machine_translated", out var mt) && mt.ValueKind == JsonValueKind.True)
                    || (a.TryGetProperty("ai_translated", out var at) && at.ValueKind == JsonValueKind.True),
                Downloads = a.TryGetProperty("download_count", out var dc) && dc.ValueKind == JsonValueKind.Number ? dc.GetInt32() : null,
                Author = a.TryGetProperty("uploader", out var up) && up.TryGetProperty("name", out var un) ? un.GetString() : null,
                Season = season,
                Episode = episode,
            });
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose() => _loginLock.Dispose();

    private static string StripTt(string id) => id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? id[2..].TrimStart('0') : id;

    private void UpdateQuota(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("remaining", out var rem) && rem.ValueKind == JsonValueKind.Number)
            {
                RemainingDownloads = rem.GetInt32();
            }

            if (root.TryGetProperty("reset_time_utc", out var reset) && reset.ValueKind == JsonValueKind.String
                && DateTime.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
            {
                QuotaResetUtc = t;
            }
        }
        catch (JsonException)
        {
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.OpenSubtitlesUsername) || string.IsNullOrWhiteSpace(cfg.OpenSubtitlesPassword))
        {
            return null; // anonymous downloads have a very small quota but work
        }

        if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc)
        {
            return _token;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc)
            {
                return _token;
            }

            using var client = CreateClient();
            using var response = await client.PostAsJsonAsync(
                "https://api.opensubtitles.com/api/v1/login",
                new { username = cfg.OpenSubtitlesUsername, password = cfg.OpenSubtitlesPassword },
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenSubtitles login failed: HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("token").GetString();
            // The token and key go to this host from now on: accept only OpenSubtitles hosts.
            if (doc.RootElement.TryGetProperty("base_url", out var b) && b.GetString() is { Length: > 0 } baseUrl
                && System.Text.RegularExpressions.Regex.IsMatch(baseUrl, @"^[a-z0-9.-]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && SafeHttp.IsAllowedHost(baseUrl, "opensubtitles.com"))
            {
                _apiHost = baseUrl;
            }

            if (doc.RootElement.TryGetProperty("user", out var u) && u.TryGetProperty("remaining_downloads", out var rd) && rd.ValueKind == JsonValueKind.Number)
            {
                RemainingDownloads = rd.GetInt32();
            }

            _tokenExpiresUtc = DateTime.UtcNow.AddHours(12);
            return _token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(OpenSubtitlesSource));
        client.MaxResponseContentBufferSize = 4 * 1024 * 1024; // API answers are small; never buffer more
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Add("Api-Key", Plugin.Instance?.Configuration.OpenSubtitlesApiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
