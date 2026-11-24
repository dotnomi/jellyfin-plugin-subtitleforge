using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Translation;

/// <summary>
/// Translates subtitle text between languages with the offline models (direct or via English).
/// </summary>
public sealed partial class TranslationService
{
    private readonly TranslationModels _models;
    private readonly ILogger<TranslationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranslationService"/> class.
    /// </summary>
    /// <param name="models">Models.</param>
    /// <param name="logger">Logger.</param>
    public TranslationService(TranslationModels models, ILogger<TranslationService> logger)
    {
        _models = models;
        _logger = logger;
    }

    /// <summary>
    /// Whether a translation from one language to another is possible.
    /// </summary>
    /// <param name="from">Source (ISO 639-1).</param>
    /// <param name="to">Target (ISO 639-1).</param>
    /// <returns>True if possible.</returns>
    public static bool CanTranslate(string from, string to) =>
        (Plugin.Instance?.Configuration ?? new PluginConfiguration()).TranslationEnabled && TranslationModels.Route(from, to) is not null;

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
    public async Task<List<string>> TranslateAsync(IReadOnlyList<string> texts, string from, string to, string modelRoot, int threads, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var route = TranslationModels.Route(from, to) ?? throw new NotSupportedException(Loc.German ? $"Keine Übersetzung {from} → {to} verfügbar" : $"No translation {from} → {to} available");
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
