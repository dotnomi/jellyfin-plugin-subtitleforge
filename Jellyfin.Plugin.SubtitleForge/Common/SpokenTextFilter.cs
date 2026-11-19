using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Keeps only spoken text: removes hearing-impaired annotations ("[SCREAMING]", "(Schüsse)", "(Frau im TV)"),
/// speaker labels ("JOHN:") and optionally song lyrics (♪ … ♪). Cues that become empty are dropped.
/// </summary>
public static partial class SpokenTextFilter
{
    /// <summary>
    /// Filters cues.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <param name="removeLyrics">Also remove lines with ♪/♫.</param>
    /// <returns>Filtered cues.</returns>
    public static List<SubtitleCue> Apply(IEnumerable<SubtitleCue> cues, bool removeLyrics)
    {
        var result = new List<SubtitleCue>();
        foreach (var cue in cues)
        {
            var text = FilterText(cue.Text, removeLyrics);
            if (text.Length > 0)
            {
                result.Add(cue with { Text = text });
            }
        }

        return result;
    }

    /// <summary>
    /// Filters one cue text (lines separated by '\n').
    /// </summary>
    /// <param name="text">Text.</param>
    /// <param name="removeLyrics">Remove lyric lines.</param>
    /// <returns>Spoken text only (may be empty).</returns>
    public static string FilterText(string text, bool removeLyrics)
    {
        var lines = new List<string>();

        // Annotations may span two lines ("(ruhige Musik\nmit kindlichem Gesang)"): remove them on the whole cue first.
        text = AnnotationRegex().Replace(text.Replace("\r", string.Empty, StringComparison.Ordinal), " ");
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            if (removeLyrics && LyricsRegex().IsMatch(line))
            {
                continue;
            }

            line = AnnotationRegex().Replace(line, " ");

            // Halves of an annotation split across two cues: "(Lied: \"I Want to See" … "von R. Thompson)".
            var bare = TagRegex().Replace(line, string.Empty).Trim();
            if ((bare.StartsWith('(') && !bare.Contains(')', StringComparison.Ordinal))
                || (bare.EndsWith(')') && !bare.Contains('(', StringComparison.Ordinal))
                || (bare.StartsWith('[') && !bare.Contains(']', StringComparison.Ordinal))
                || (bare.EndsWith(']') && !bare.Contains('[', StringComparison.Ordinal)))
            {
                continue;
            }
            line = SpeakerRegex().Replace(line, string.Empty);
            line = SpacesRegex().Replace(line, " ").Trim();
            line = line.Replace("<i> ", "<i>", StringComparison.Ordinal).Replace(" </i>", "</i>", StringComparison.Ordinal);

            // "<i></i>", lone dashes or punctuation left over from removed annotations.
            var visible = TagRegex().Replace(line, string.Empty).Trim();
            if (visible.Length == 0 || LeftoverRegex().IsMatch(visible))
            {
                continue;
            }

            lines.Add(line);
        }

        // A single remaining dialogue line does not need its dash.
        if (lines.Count == 1)
        {
            lines[0] = DialogueDashRegex().Replace(lines[0], string.Empty);
        }

        return string.Join('\n', lines);
    }

    // [ ... ], ( ... ), 【 ... 】, （ ... ） – up to 120 chars (may include one line break), no nested brackets.
    [GeneratedRegex(@"\[[^\[\]]{1,120}\]|\([^()]{1,120}\)|【[^【】]{1,120}】|（[^（）]{1,120}）")]
    private static partial Regex AnnotationRegex();

    // Speaker labels in capitals at the start of a line (after an optional dialogue dash): "JOHN:", "- MAN 2:"
    [GeneratedRegex(@"(?<=^\s*(?:-\s*)?(?:<i>)?)\p{Lu}[\p{Lu}\d .'’-]{1,24}:\s+")]
    private static partial Regex SpeakerRegex();

    [GeneratedRegex(@"[♪♫]")]
    private static partial Regex LyricsRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^[-–—.,:;!?…\s]*$")]
    private static partial Regex LeftoverRegex();

    [GeneratedRegex(@"^\s*-\s*")]
    private static partial Regex DialogueDashRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();
}
