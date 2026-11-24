using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Language code helpers (ISO 639-1 ↔ ISO 639-2/B ↔ ISO 639-2/T).
/// </summary>
public static class Lang
{
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = new[] { "ger", "deu" },
        ["en"] = new[] { "eng" },
        ["fr"] = new[] { "fre", "fra" },
        ["es"] = new[] { "spa" },
        ["it"] = new[] { "ita" },
        ["nl"] = new[] { "dut", "nld" },
        ["ja"] = new[] { "jpn" },
        ["pt"] = new[] { "por" },
        ["pl"] = new[] { "pol" },
        ["tr"] = new[] { "tur" },
        ["ru"] = new[] { "rus" },
        ["sv"] = new[] { "swe" },
        ["da"] = new[] { "dan" },
        ["no"] = new[] { "nor" },
        ["fi"] = new[] { "fin" },
        ["cs"] = new[] { "cze", "ces" },
        ["zh"] = new[] { "chi", "zho" },
        ["ko"] = new[] { "kor" },
    };

    /// <summary>
    /// A language code that is safe to use in a file name: the code itself if it is a known language written as
    /// letters (optionally with a region, e.g. "pt-BR"), otherwise "und". Container tags are untrusted input.
    /// </summary>
    /// <param name="code">Language from a stream.</param>
    /// <returns>Safe token.</returns>
    public static string FileToken(string? code) =>
        code is not null && ToTwoLetter(code) is not null
            && System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Za-z]{2,3}(-[A-Za-z]{2,4})?$")
            ? code
            : "und";

    /// <summary>
    /// Converts any code to ISO 639-1 (two letters, lowercase). Returns null if unknown.
    /// </summary>
    /// <param name="code">Language code or name.</param>
    /// <returns>Two-letter code.</returns>
    public static string? ToTwoLetter(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        code = code.Trim();
        if (Known.ContainsKey(code))
        {
            return code.ToLowerInvariant();
        }

        foreach (var (two, threes) in Known)
        {
            if (threes.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                return two;
            }
        }

        try
        {
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(c => c.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
                    || c.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
                    || c.EnglishName.Equals(code, StringComparison.OrdinalIgnoreCase));
            return culture?.TwoLetterISOLanguageName;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts to ISO 639-2/B (what Jellyfin uses for file names, e.g. "ger").
    /// </summary>
    /// <param name="code">Language code.</param>
    /// <returns>Three-letter code or the input.</returns>
    public static string ToThreeLetter(string code)
    {
        var two = ToTwoLetter(code);
        if (two is not null && Known.TryGetValue(two, out var threes))
        {
            return threes[0];
        }

        return code;
    }

    /// <summary>
    /// Whether two codes denote the same language.
    /// </summary>
    /// <param name="a">First.</param>
    /// <param name="b">Second.</param>
    /// <returns>True if same language.</returns>
    public static bool Same(string? a, string? b)
    {
        var x = ToTwoLetter(a);
        return x is not null && x == ToTwoLetter(b);
    }
}
