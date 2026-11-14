using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Translation;

/// <summary>
/// Thrown when the DeepL character quota is used up (HTTP 456).
/// </summary>
public class DeepLQuotaException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeepLQuotaException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    public DeepLQuotaException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// DeepL API (Free or Pro, decided by the key) for subtitle translation. Lines are sent in batches with the
/// preceding lines as (unbilled) context, which helps with sentence fragments.
/// </summary>
public sealed class DeepLTranslator
{
    private const int BatchSize = 40;
    private const int ContextLines = 4;

    private static readonly HashSet<string> SourceLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "he", "hu", "id", "it", "ja", "ko", "lt", "lv",
        "nb", "no", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "th", "tr", "uk", "vi", "zh",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DeepLTranslator> _logger;
    private readonly Func<string?> _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepLTranslator"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public DeepLTranslator(IHttpClientFactory httpClientFactory, ILogger<DeepLTranslator> logger)
        : this(httpClientFactory, logger, () => Key)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepLTranslator"/> class with an explicit key source (tests).
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="key">Key provider.</param>
    internal DeepLTranslator(IHttpClientFactory httpClientFactory, ILogger<DeepLTranslator> logger, Func<string?> key)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _key = key;
    }

    /// <summary>Gets a value indicating whether a key is configured.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Key);

    private static string? Key => (Plugin.Instance?.Configuration ?? new PluginConfiguration()).DeepLApiKey?.Trim();

    /// <summary>
    /// Whether DeepL translates this direction.
    /// </summary>
    /// <param name="from">ISO 639-1 source.</param>
    /// <param name="to">ISO 639-1 target.</param>
    /// <returns>True if supported.</returns>
    public static bool Supports(string from, string to) => from != to && SourceLanguages.Contains(from) && SourceLanguages.Contains(to);

    /// <summary>
    /// DeepL target code (English and Portuguese need a variant, Chinese a script).
    /// </summary>
    /// <param name="language">ISO 639-1.</param>
    /// <returns>Code.</returns>
    public static string TargetCode(string language) => language switch
    {
        "en" => "EN-US",
        "pt" => "PT-PT",
        "zh" => "ZH-HANS",
        "no" => "NB",
        _ => language.ToUpperInvariant(),
    };

    /// <summary>
    /// Builds the API base URL: keys of the free plan end with ":fx".
    /// </summary>
    /// <param name="key">API key.</param>
    /// <returns>Base URL.</returns>
    public static string BaseUrl(string key) => key.EndsWith(":fx", StringComparison.Ordinal) ? "https://api-free.deepl.com" : "https://api.deepl.com";

    /// <summary>
    /// Reads the character usage of the account.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Used and limit, or null if not configured / failed.</returns>
    public async Task<(long Used, long Limit)?> GetUsageAsync(CancellationToken cancellationToken)
    {
        var key = _key();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        using var client = CreateClient(key);
        using var response = await client.GetAsync(BaseUrl(key) + "/v2/usage", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("DeepL usage request failed: HTTP {Status}", (int)response.StatusCode);
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return (doc.RootElement.GetProperty("character_count").GetInt64(), doc.RootElement.GetProperty("character_limit").GetInt64());
    }

    /// <summary>
    /// Translates lines (order and count preserved).
    /// </summary>
    /// <param name="texts">Lines.</param>
    /// <param name="from">ISO 639-1 source.</param>
    /// <param name="to">ISO 639-1 target.</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Translations.</returns>
    public async Task<List<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var key = _key() ?? throw new InvalidOperationException("DeepL API key missing");
        using var client = CreateClient(key);
        var result = new List<string>(texts.Count);
        for (var start = 0; start < texts.Count; start += BatchSize)
        {
            var batch = texts.Skip(start).Take(BatchSize).ToList();
            var context = string.Join("\n", texts.Skip(Math.Max(0, start - ContextLines)).Take(Math.Min(ContextLines, start)));
            var body = new Dictionary<string, object>
            {
                ["text"] = batch,
                ["source_lang"] = from.ToUpperInvariant(),
                ["target_lang"] = TargetCode(to),
                ["preserve_formatting"] = true,
                ["split_sentences"] = "nonewlines",
            };
            if (context.Length > 0)
            {
                body["context"] = context;
            }

            result.AddRange(await SendAsync(client, key, body, batch.Count, cancellationToken).ConfigureAwait(false));
            progress?.Report((double)result.Count / texts.Count);
        }

        return result;
    }

    private static HttpClient CreateClientCore(IHttpClientFactory factory, string key)
    {
        var client = factory.CreateClient(nameof(DeepLTranslator));
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SubtitleForge/0.2");
        return client;
    }

    private HttpClient CreateClient(string key) => CreateClientCore(_httpClientFactory, key);

    private async Task<List<string>> SendAsync(HttpClient client, string key, Dictionary<string, object> body, int expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await client.PostAsJsonAsync(BaseUrl(key) + "/v2/translate", body, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 456)
            {
                throw new DeepLQuotaException("DeepL-Kontingent aufgebraucht");
            }

            if ((status == 429 || status == 529 || status >= 500) && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException("DeepL lehnt den API-Key ab (403)");
            }

            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var list = doc.RootElement.GetProperty("translations").EnumerateArray().Select(t => t.GetProperty("text").GetString() ?? string.Empty).ToList();
            if (list.Count != expected)
            {
                throw new InvalidOperationException($"DeepL returned {list.Count} translations for {expected} lines");
            }

            return list;
        }
    }
}
