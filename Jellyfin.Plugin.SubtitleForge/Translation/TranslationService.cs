using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Translation;

/// <summary>
/// Translates subtitle text between languages with the offline models (direct or via English).
/// </summary>
public sealed partial class TranslationService
{
    private readonly TranslationModels _models;
    private readonly DeepLTranslator _deepL;
    private readonly ILogger<TranslationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationService"/> class.
    /// </summary>
    /// <param name="models">Models.</param>
    /// <param name="deepL">DeepL client.</param>
    /// <param name="logger">Logger.</param>
    public TranslationService(TranslationModels models, DeepLTranslator deepL, ILogger<TranslationService> logger)
    {
        _models = models;
        _deepL = deepL;
        _logger = logger;
    }

    /// <summary>
    /// Whether a translation from one language to another is possible.
    /// </summary>
    /// <param name="from">Source (ISO 639-1).</param>
    /// <param name="to">Target (ISO 639-1).</param>
    /// <returns>True if possible.</returns>
    public static bool CanTranslate(string from, string to) => RouteCost(from, to) is not null;

    /// <summary>
    /// Number of translation steps from one language to another (DeepL: always direct), or null if impossible.
    /// </summary>
    /// <param name="from">Source.</param>
    /// <param name="to">Target.</param>
    /// <returns>Steps.</returns>
    public static int? RouteCost(string from, string to)
    {
        if (!(Plugin.Instance?.Configuration ?? new PluginConfiguration()).TranslationEnabled)
        {
            return null;
        }

        if (DeepLTranslator.IsConfigured && DeepLTranslator.Supports(from, to))
        {
            return 1;
        }

        return TranslationModels.Route(from, to)?.Count;
    }

    /// <summary>
    /// Splits text into sentences (Latin and CJK punctuation) – models translate single sentences much better.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <returns>Sentences.</returns>
    public static List<string> SplitSentences(string text) =>
        SentenceRegex().Matches(text).Select(m => m.Value.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>
    /// Fixes casing of FuguMT output ("i think he's ..." → "I think he's ...").
    /// </summary>
    /// <param name="text">Text.</param>
    /// <param name="language">Language of the text.</param>
    /// <returns>Fixed text.</returns>
    public static string FixCasing(string text, string language)
    {
        if (text.Length == 0 || !char.IsLower(text[0]))
        {
            return text;
        }

        var t = char.ToUpperInvariant(text[0]) + text[1..];
        if (language == "en")
        {
            t = LowerIRegex().Replace(t, "I");
            t = SentenceStartRegex().Replace(t, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
        }

        return t;
    }

    /// <summary>
    /// Translates a list of texts (e.g. Whisper segments). Order and count are preserved.
    /// </summary>
    /// <param name="texts">Texts.</param>
    /// <param name="from">Source language.</param>
    /// <param name="to">Target language.</param>
    /// <param name="modelRoot">Folder for models.</param>
    /// <param name="threads">CPU threads.</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Translations.</returns>
    /// <returns>Translations and the engine used ("DeepL" or "offline").</returns>
    public async Task<(List<string> Texts, string Engine)> TranslateAsync(IReadOnlyList<string> texts, string from, string to, string modelRoot, int threads, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (DeepLTranslator.IsConfigured && DeepLTranslator.Supports(from, to))
        {
            try
            {
                var needed = texts.Sum(t => t.Length);
                var usage = await _deepL.GetUsageAsync(cancellationToken).ConfigureAwait(false);
                if (usage is { } u && u.Limit > 0 && u.Limit - u.Used < needed)
                {
                    _logger.LogWarning("SubtitleForge: DeepL quota too low ({Used}/{Limit}, need {Needed}) – using offline models", u.Used, u.Limit, needed);
                }
                else
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var translated = await _deepL.TranslateAsync(texts, from, to, progress, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("SubtitleForge: translated {Count} lines {From}-{To} with DeepL in {Seconds:F0}s ({Chars} characters)", texts.Count, from, to, sw.Elapsed.TotalSeconds, needed);
                    return (translated, "DeepL");
                }
            }
            catch (Exception ex) when (ex is DeepLQuotaException or HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("SubtitleForge: DeepL failed ({Message}) – using offline models", ex.Message);
            }
        }

        return (await TranslateOfflineAsync(texts, from, to, modelRoot, threads, progress, cancellationToken).ConfigureAwait(false), "offline");
    }

    /// <summary>
    /// Translates with the offline models (direct or via English).
    /// </summary>
    /// <param name="texts">Texts.</param>
    /// <param name="from">Source language.</param>
    /// <param name="to">Target language.</param>
    /// <param name="modelRoot">Folder for models.</param>
    /// <param name="threads">CPU threads.</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Translations.</returns>
    public async Task<List<string>> TranslateOfflineAsync(IReadOnlyList<string> texts, string from, string to, string modelRoot, int threads, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var route = TranslationModels.Route(from, to) ?? throw new NotSupportedException($"Keine Übersetzung {from} → {to} verfügbar");
        var current = texts.ToList();
        for (var step = 0; step < route.Count; step++)
        {
            var pair = route[step];
            var dir = await _models.EnsureAsync(pair, modelRoot, cancellationToken).ConfigureAwait(false);
            var target = pair[3..];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            current = await Task.Run(
                () =>
                {
                    using var translator = new MarianTranslator(dir, threads);
                    var result = new List<string>(current.Count);
                    for (var i = 0; i < current.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sb = new StringBuilder();
                        foreach (var sentence in SplitSentences(current[i]))
                        {
                            var t = FixCasing(translator.Translate(sentence), target);
                            if (t.Length > 0)
                            {
                                sb.Append(sb.Length > 0 ? " " : string.Empty).Append(t);
                            }
                        }

                        result.Add(sb.ToString());
                        progress?.Report(((step + ((i + 1.0) / current.Count)) / route.Count));
                    }

                    return result;
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("SubtitleForge: translated {Count} lines {Pair} in {Seconds:F0}s", current.Count, pair, sw.Elapsed.TotalSeconds);
        }

        return current;
    }

    [GeneratedRegex(@"[^.!?。！？…]+(?:[.!?。！？…]+[""'」』)]*|$)")]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"(?<=^|\s)i(?=\s|'|$)")]
    private static partial Regex LowerIRegex();

    [GeneratedRegex(@"([.!?]\s+)(\p{Ll})")]
    private static partial Regex SentenceStartRegex();
}
