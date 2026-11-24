using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubtitleForge.Common;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Second OCR correction pass over a whole subtitle: fixes glyph confusions (I/l, rn/m, cl/d, 0/O, 1/l, vv/w and,
/// for German, ii/ü) in words that are neither in the language's word list nor frequent in the file, when exactly one
/// confusion variant is a known word. Known words come from a compact embedded word list (de, en; see
/// THIRD-PARTY-NOTICES.md) and from the subtitle itself (words seen at least twice). Umlauts are never guessed.
/// </summary>
public static partial class OcrDictionary
{
    private const int MaxVariants = 32;
    private const int MaxSubstitutions = 2;

    private static readonly Lazy<HashSet<string>> English = new(() => Load("en"));
    private static readonly Lazy<HashSet<string>> German = new(() => Load("de"));

    /// <summary>
    /// Corrects all cue texts of one subtitle.
    /// </summary>
    /// <param name="texts">Cue texts (may contain line breaks), in order.</param>
    /// <param name="language">Subtitle language ("de", "en", another code or null); selects the word list.</param>
    /// <returns>Corrected texts, same count and order.</returns>
    public static IReadOnlyList<string> Correct(IReadOnlyList<string> texts, string? language)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var lang = Lang.ToTwoLetter(language);
        var context = new Context(
            lang switch
            {
                "de" => German.Value,
                "en" => English.Value,
                _ => null,
            },
            string.Equals(lang, "de", StringComparison.Ordinal));

        foreach (var text in texts)
        {
            ForEachToken(text, (_, token) =>
            {
                var key = token.ToLowerInvariant();
                context.Frequency[key] = context.Frequency.GetValueOrDefault(key) + 1;
            });
        }

        var result = new string[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            StringBuilder? sb = null;
            var copied = 0;
            ForEachToken(text, (index, token) =>
            {
                if (!context.Cache.TryGetValue(token, out var fixedToken))
                {
                    fixedToken = Fix(token, context);
                    context.Cache[token] = fixedToken;
                }

                if (fixedToken is null || IsProtectedPosition(text, index, token))
                {
                    return;
                }

                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copied, index - copied).Append(fixedToken);
                copied = index + token.Length;
            });

            result[i] = sb is null ? text : sb.Append(text, copied, text.Length - copied).ToString();
        }

        return result;
    }

    /// <summary>
    /// Calls <paramref name="action"/> for every word token outside URLs / mail addresses.
    /// </summary>
    private static void ForEachToken(string text, Action<int, string> action)
    {
        var urls = UrlRegex().Matches(text);
        foreach (Match m in TokenRegex().Matches(text))
        {
            var inUrl = false;
            foreach (Match u in urls)
            {
                if (m.Index < u.Index + u.Length && u.Index < m.Index + m.Length)
                {
                    inUrl = true;
                    break;
                }
            }

            if (!inUrl)
            {
                action(m.Index, m.Value);
            }
        }
    }

    /// <summary>
    /// Tokens after an apostrophe ("we'll", "geht's") and tokens with digits next to number separators
    /// ("19:45", "3.5km") are left alone.
    /// </summary>
    private static bool IsProtectedPosition(string text, int index, string token)
    {
        if (index > 0 && text[index - 1] is '\'' or '’')
        {
            return true;
        }

        if (!token.AsSpan().ContainsAnyInRange('0', '9'))
        {
            return false;
        }

        var end = index + token.Length;
        return (index > 1 && text[index - 1] is ':' or '.' or ',' or '/' && char.IsDigit(text[index - 2]))
            || (end + 1 < text.Length && text[end] is ':' or '.' or ',' or '/' && char.IsDigit(text[end + 1]));
    }

    /// <summary>
    /// Returns the corrected token, or null when it stays as it is.
    /// </summary>
    private static string? Fix(string token, Context context)
    {
        if (token.Length < 2)
        {
            return null;
        }

        int letters = 0, digits = 0, upper = 0;
        foreach (var c in token)
        {
            if (char.IsDigit(c))
            {
                if (c is not ('0' or '1'))
                {
                    return null;
                }

                digits++;
            }
            else if (char.IsLetter(c))
            {
                letters++;
                if (char.IsUpper(c))
                {
                    upper++;
                }
            }
        }

        // Numbers, ordinals ("1st"), short or digit-heavy tokens ("l0l", "10th") look intentional.
        if (digits > 0 && (letters == 0 || token.Length < 4 || digits * 2 >= token.Length))
        {
            return null;
        }

        var allCaps = letters >= 2 && upper == letters;
        if (allCaps && (token.Length < 3 || digits == 0))
        {
            // "OK", "TV", "IKEA": capital I in caps is a real I; only digit confusions are fixed in caps.
            return null;
        }

        var lower = token.ToLowerInvariant();
        var regularCase = allCaps
            || string.Equals(lower, token, StringComparison.Ordinal)
            || (char.IsUpper(token[0]) && token.AsSpan(1).SequenceEqual(lower.AsSpan(1)));
        var frequent = false;
        if (regularCase && digits == 0)
        {
            if (context.Words?.Contains(lower) == true)
            {
                return null;
            }

            // Frequent in the file: a real word (or a name if capitalised) the word list does not know - or an OCR
            // error the recognizer repeats. Only stronger evidence changes it then (see IsKnown).
            var count = context.Frequency.GetValueOrDefault(lower);
            frequent = count >= 3 || (count >= 2 && char.IsUpper(token[0]));
        }

        // A frequent lowercase token (or one starting with the I/l glyph) may still match the word list; a frequent
        // capitalised one ("Clint", "Holrnes") is treated like a name and only yields to a far more frequent file word.
        var trustWordList = !frequent || !char.IsUpper(token[0]) || token[0] is 'I' or '1';

        var options = new string[token.Length];
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            options[i] = allCaps
                ? c switch
                {
                    '0' => "o",
                    '1' => "i",
                    _ => char.ToLowerInvariant(c).ToString(),
                }
                : (i, c) switch
                {
                    // A vertical bar at the start is either a capital I or a lowercase l.
                    (0, 'I' or '1' or 'l') => c == 'l' ? "li" : "il",
                    (_, 'I' or '1') => "l",
                    (_, '0' or 'O') => "o",
                    _ => char.ToLowerInvariant(c).ToString(),
                };
        }

        var variants = new List<Variant>();
        Generate(token, options, 0, new StringBuilder(), 0, false, allCaps, context.German, variants);

        for (var cost = 0; cost <= MaxSubstitutions; cost++)
        {
            Variant? found = null;
            var ambiguous = false;
            foreach (var v in variants)
            {
                if (v.Cost != cost || !IsKnown(v.Text, lower, frequent, trustWordList, context))
                {
                    continue;
                }

                if (found is null)
                {
                    found = v;
                }
                else if (!string.Equals(found.Value.Text, v.Text, StringComparison.Ordinal))
                {
                    ambiguous = true;
                }
            }

            if (ambiguous)
            {
                return null;
            }

            if (found is { } hit)
            {
                var output = Recase(token, hit, allCaps);
                return string.Equals(output, token, StringComparison.Ordinal) ? null : output;
            }
        }

        return null;
    }

    private static bool IsKnown(string candidate, string tokenLower, bool frequent, bool trustWordList, Context context)
    {
        if (trustWordList && context.Words?.Contains(candidate) == true)
        {
            return true;
        }

        // The token itself only counts via the word list (its own occurrences would confirm the error); a word from
        // the file must be seen at least twice and more often than the token (three times as often if the token is
        // frequent itself).
        var count = context.Frequency.GetValueOrDefault(candidate);
        var tokenCount = context.Frequency.GetValueOrDefault(tokenLower);
        return !string.Equals(candidate, tokenLower, StringComparison.Ordinal)
            && count >= 2
            && (frequent ? count >= 3 * tokenCount : count > tokenCount);
    }

    /// <summary>
    /// Enumerates lowercase variants: per-position options plus multi-letter confusions, at most
    /// <see cref="MaxVariants"/> variants and <see cref="MaxSubstitutions"/> multi-letter substitutions.
    /// </summary>
    private static void Generate(string token, string[] options, int pos, StringBuilder sb, int cost, bool firstIsCapitalI, bool allCaps, bool german, List<Variant> variants)
    {
        if (variants.Count >= MaxVariants)
        {
            return;
        }

        if (pos == options.Length)
        {
            variants.Add(new Variant(sb.ToString(), cost, firstIsCapitalI));
            return;
        }

        var len = sb.Length;
        foreach (var c in options[pos])
        {
            sb.Append(c);
            Generate(token, options, pos + 1, sb, cost, pos == 0 ? c == 'i' && options[0].Length > 1 : firstIsCapitalI, allCaps, german, variants);
            sb.Length = len;
        }

        // Multi-letter confusions only between lowercase glyphs ("cl" looks like "d", "Cl" does not look like "D", which
        // also keeps names like "Clive" intact) and not on the ambiguous I/l start position.
        if (allCaps || cost >= MaxSubstitutions || options[pos].Length != 1 || !IsLowercaseGlyph(token, pos))
        {
            return;
        }

        var here = options[pos][0];
        var next = pos + 1 < options.Length && options[pos + 1].Length == 1 && IsLowercaseGlyph(token, pos + 1) ? options[pos + 1][0] : '\0';
        var replacement = (here, next) switch
        {
            ('r', 'n') => "m",
            ('c', 'l') => "d",
            ('v', 'v') => "w",
            ('i', 'i') when german => "ü",
            _ => null,
        };
        if (replacement is not null)
        {
            sb.Append(replacement);
            Generate(token, options, pos + 2, sb, cost + 1, firstIsCapitalI, allCaps, german, variants);
            sb.Length = len;
        }

        if (here == 'm')
        {
            sb.Append("rn");
            Generate(token, options, pos + 1, sb, cost + 1, firstIsCapitalI, allCaps, german, variants);
            sb.Length = len;
        }
    }

    private static bool IsLowercaseGlyph(string token, int pos)
        => char.IsLower(token[pos]) || (pos > 0 && token[pos] is 'I' or '1');

    /// <summary>
    /// Applies the original capitalisation pattern to a lowercase variant.
    /// </summary>
    private static string Recase(string token, Variant variant, bool allCaps)
    {
        if (allCaps)
        {
            return variant.Text.ToUpperInvariant();
        }

        var first = token[0];
        var capitalise = first switch
        {
            'I' or '1' or 'l' => variant.FirstIsCapitalI,
            '0' or 'O' => true,
            _ => char.IsUpper(first),
        };
        return capitalise
            ? string.Concat(variant.Text[..1].ToUpperInvariant(), variant.Text.AsSpan(1))
            : variant.Text;
    }

    private static HashSet<string> Load(string lang)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Jellyfin.Plugin.SubtitleForge.Ocr.Dictionaries.{lang}.txt.gz");
        if (stream is null)
        {
            return words;
        }

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0 && line[0] != '#')
            {
                words.Add(line);
            }
        }

        return words;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?:https?://|www\.)\S+|\S+@\S+\.\S+|\b[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)*\.(?:com|net|org|de|at|ch|uk|eu|io|tv|info)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    private readonly record struct Variant(string Text, int Cost, bool FirstIsCapitalI);

    private sealed class Context(HashSet<string>? words, bool german)
    {
        public HashSet<string>? Words { get; } = words;

        public bool German { get; } = german;

        public Dictionary<string, int> Frequency { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string?> Cache { get; } = new(StringComparer.Ordinal);
    }
}
