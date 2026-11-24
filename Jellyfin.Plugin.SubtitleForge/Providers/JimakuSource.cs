using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Jimaku (jimaku.cc): anime subtitles, mostly Japanese, some English. Needs a free API key.
/// API: GET /api/entries/search (by TMDb id or title) → GET /api/entries/{id}/files?episode=N.
/// </summary>
public partial class JimakuSource : ISubtitleSource
{
    private const string ApiBase = "https://jimaku.cc/api";
    private static readonly string[] Supported = { ".srt", ".ass", ".ssa", ".zip" };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JimakuSource> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JimakuSource"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public JimakuSource(IHttpClientFactory httpClientFactory, ILogger<JimakuSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Jimaku";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    private static string? ApiKey => Plugin.Instance?.Configuration.JimakuApiKey;

    /// <summary>
    /// Guesses the language of a Jimaku file from its name (most files are Japanese).
    /// </summary>
    /// <param name="fileName">File name.</param>
    /// <returns>ISO 639-1 code.</returns>
    public static string GuessLanguage(string fileName)
    {
        if (EnglishRegex().IsMatch(fileName))
        {
            return "en";
        }

        return "ja";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, CancellationToken cancellationToken)
    {
        if (!IsConfigured || !query.Languages.Any(l => l is "ja" or "en"))
        {
            return Array.Empty<SubtitleCandidate>();
        }

        using var client = CreateClient();
        var tmdb = query.IsEpisode ? query.SeriesTmdbId : query.TmdbId;
        var search = !string.IsNullOrEmpty(tmdb)
            ? $"{ApiBase}/entries/search?tmdb_id={(query.IsEpisode ? "tv" : "movie")}:{Uri.EscapeDataString(tmdb)}"
            : $"{ApiBase}/entries/search?query={Uri.EscapeDataString(query.Title)}";
        using var entriesDoc = await GetJsonAsync(client, search, cancellationToken).ConfigureAwait(false);
        if (entriesDoc is null || entriesDoc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SubtitleCandidate>();
        }

        var result = new List<SubtitleCandidate>();
        foreach (var entry in entriesDoc.RootElement.EnumerateArray().Take(2))
        {
            if (!entry.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
            {
                continue;
            }

            var filesUrl = $"{ApiBase}/entries/{id.ToString(CultureInfo.InvariantCulture)}/files"
                + (query.IsEpisode && query.Episode is { } ep ? "?episode=" + ep.ToString(CultureInfo.InvariantCulture) : string.Empty);
            using var filesDoc = await GetJsonAsync(client, filesUrl, cancellationToken).ConfigureAwait(false);
            if (filesDoc is null || filesDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var f in filesDoc.RootElement.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var url = f.TryGetProperty("url", out var u) ? u.GetString() : null;
                var size = f.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
                if (url is null || !Supported.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) || size is > 0 and < 500)
                {
                    continue; // RAR/7z are not supported; tiny files are broken uploads
                }

                var lang = GuessLanguage(name);
                if (!query.Languages.Contains(lang))
                {
                    continue;
                }

                var (season, episode) = ReleaseInfo.ParseEpisode(name);
                result.Add(new SubtitleCandidate
                {
                    Source = Name,
                    Reference = url,
                    Language = lang,
                    Releases = new List<string> { name },
                    Season = season ?? (query.IsEpisode ? query.Season : null),
                    Episode = episode ?? (query.IsEpisode ? query.Episode : null), // the API already filtered by episode
                    IsPack = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<(byte[] Data, string? FileName)> DownloadAsync(SubtitleCandidate candidate, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(candidate.Reference, cancellationToken).ConfigureAwait(false);
        ThrowIfLimited(response);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (data, candidate.Releases.FirstOrDefault());
    }

    private static void ThrowIfLimited(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return;
        }

        var wait = response.Headers.TryGetValues("x-ratelimit-reset-after", out var v)
            && double.TryParse(v.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 60;
        throw new QuotaExceededException("Jimaku rate limit", DateTime.UtcNow.AddSeconds(Math.Clamp(wait, 1, 3600)));
    }

    private async Task<JsonDocument?> GetJsonAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        ThrowIfLimited(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Jimaku request failed: HTTP {Status} {Body}", (int)response.StatusCode, body.Length > 200 ? body[..200] : body);
            return null;
        }

        return JsonDocument.Parse(body);
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(nameof(JimakuSource));
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SubtitleForge-Jellyfin-Plugin/0.3");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", ApiKey);
        return client;
    }

    [GeneratedRegex(@"(\b|[._\[\(-])(en|eng|english)(\b|[._\]\)-])", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishRegex();
}
