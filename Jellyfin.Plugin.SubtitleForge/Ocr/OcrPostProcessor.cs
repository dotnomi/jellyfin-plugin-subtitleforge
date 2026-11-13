using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Fixes typical OCR confusions in subtitle text.
/// </summary>
public static partial class OcrPostProcessor
{
    /// <summary>
    /// Cleans one recognized line.
    /// </summary>
    /// <param name="line">Raw OCR line.</param>
    /// <returns>Fixed line.</returns>
    public static string Fix(string line)
    {
        var t = line.Trim();

        // '|' is almost always a capital I in subtitles.
        t = t.Replace('|', 'I');

        // Standalone lowercase l between spaces / at start before a capitalised-looking context → I ("l am", "l'm").
        t = LoneLRegex().Replace(t, "I");

        // Capital I inside lowercase context is an l: "it'Il" → "it'll", "wiIl" → "will", "AIles" stays.
        t = ApostropheIlRegex().Replace(t, "'ll");
        t = InnerCapitalIRegex().Replace(t, "l");

        // No space before . , ! ? : ; and none after opening quotes.
        t = SpaceBeforePunctRegex().Replace(t, "$1");

        // Dialogue dash: "-Text" → "- Text" is a style choice; normalise to "- ".
        t = DialogueDashRegex().Replace(t, "- ");

        // Ellipsis made of separate dots.
        t = t.Replace(". . .", "...", System.StringComparison.Ordinal);

        return t;
    }

    [GeneratedRegex(@"(?<=^|[\s""])l(?=\s|'m\b|'ll\b|'ve\b|'d\b)")]
    private static partial Regex LoneLRegex();

    [GeneratedRegex(@"'(Il|lI|II)\b")]
    private static partial Regex ApostropheIlRegex();

    [GeneratedRegex(@"(?<=\p{Ll})I(?=\p{Ll}|l\b|\b)")]
    private static partial Regex InnerCapitalIRegex();

    [GeneratedRegex(@"\s+([.,!?:;])(?=\s|$)")]
    private static partial Regex SpaceBeforePunctRegex();

    [GeneratedRegex(@"^[-–—]\s*")]
    private static partial Regex DialogueDashRegex();
}
