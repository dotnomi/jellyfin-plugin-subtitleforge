using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// A single subtitle cue.
/// </summary>
/// <param name="StartMs">Start in ms.</param>
/// <param name="EndMs">End in ms.</param>
/// <param name="Text">Text, lines separated by '\n'. Only &lt;i&gt; tags are kept.</param>
public sealed record SubtitleCue(long StartMs, long EndMs, string Text);

/// <summary>
/// SRT reading/writing and normalisation into a clean, client-neutral form.
/// </summary>
public static partial class Srt
{
    /// <summary>
    /// Normalises cue text so every client renders it with its own (uniform) style:
    /// removes font/color/size tags and ASS override blocks, keeps italics, trims whitespace.
    /// </summary>
    /// <param name="text">Raw text.</param>
    /// <returns>Clean text.</returns>
    public static string NormalizeText(string text)
    {
        var t = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        t = t.Replace("\\N", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\h", " ", StringComparison.Ordinal);
        t = AssOverrideRegex().Replace(t, string.Empty);
        t = ItalicOpenRegex().Replace(t, "\u0001");
        t = ItalicCloseRegex().Replace(t, "\u0002");
        t = TagRegex().Replace(t, string.Empty);
        t = t.Replace("\u0001", "<i>", StringComparison.Ordinal).Replace("\u0002", "</i>", StringComparison.Ordinal);

        var lines = t.Split('\n')
            .Select(l => MultiSpaceRegex().Replace(l, " ").Trim())
            .Where(l => l.Length > 0 && l != "<i></i>");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Parses SRT content.
    /// </summary>
    /// <param name="content">File content.</param>
    /// <returns>Cues.</returns>
    public static List<SubtitleCue> Parse(string content)
    {
        var cues = new List<SubtitleCue>();
        var blocks = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('﻿').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timeIdx = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timeIdx < 0)
            {
                continue;
            }

            if (lines[timeIdx].Length > 100)
            {
                continue; // a timing line is short; long ones are junk (and would make the regex slow)
            }

            var m = TimeLineRegex().Match(lines[timeIdx]);
            if (!m.Success)
            {
                continue;
            }

            // Untrusted input: skip absurd cues (keeps the text regexes fast) and unparsable times.
            var text = string.Join('\n', lines.Skip(timeIdx + 1));
            var start = ParseTime(m.Groups[1].Value);
            var end = ParseTime(m.Groups[2].Value);
            if (text.Length > MaxCueLength || start < 0 || end < 0)
            {
                continue;
            }

            cues.Add(new SubtitleCue(start, end, text));
        }

        return cues;
    }

    /// <summary>
    /// Writes cues as SRT (normalised, sorted, empty cues dropped).
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <returns>SRT content.</returns>
    public static string Write(IEnumerable<SubtitleCue> cues)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is { SpokenTextOnly: true })
        {
            cues = SpokenTextFilter.Apply(cues.Select(c => c with { Text = NormalizeText(c.Text) }), config.RemoveLyrics);
        }
        else if (config is { RemoveLyrics: true })
        {
            cues = SpokenTextFilter.Apply(cues.Select(c => c with { Text = NormalizeText(c.Text) }), true);
        }

        var sb = new StringBuilder();
        var n = 1;
        foreach (var cue in cues.OrderBy(c => c.StartMs))
        {
            var text = NormalizeText(cue.Text);
            if (text.Length == 0 || cue.EndMs <= cue.StartMs)
            {
                continue;
            }

            sb.Append(n++).Append('\n')
              .Append(FormatTime(cue.StartMs)).Append(" --> ").Append(FormatTime(cue.EndMs)).Append('\n')
              .Append(text).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats ms as SRT time.
    /// </summary>
    /// <param name="ms">Milliseconds.</param>
    /// <returns>hh:mm:ss,fff.</returns>
    public static string FormatTime(long ms)
    {
        ms = Math.Clamp(ms, 0, (100L * 3600000) - 1);
        var ts = TimeSpan.FromMilliseconds(ms);
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", (int)ts.TotalHours, ts.Minutes, ts.Seconds, ts.Milliseconds);
    }

    /// <summary>Longest cue text that is accepted from a file.</summary>
    public const int MaxCueLength = 2000;

    private static long ParseTime(string s)
    {
        var p = s.Replace('.', ',').Split(':', ',');
        if (p.Length < 4
            || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h > 99
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m > 59
            || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sec) || sec > 59
            || !int.TryParse(p[3].PadRight(3, '0')[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
        {
            return -1;
        }

        return (h * 3600000L) + (m * 60000L) + (sec * 1000L) + ms;
    }

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssOverrideRegex();

    [GeneratedRegex(@"<\s*i\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ItalicOpenRegex();

    [GeneratedRegex(@"<\s*/\s*i\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ItalicCloseRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"(\d{1,3}:\d{1,2}:\d{1,2}[,.]\d{1,3})\s*-->\s*(\d{1,3}:\d{1,2}:\d{1,2}[,.]\d{1,3})")]
    private static partial Regex TimeLineRegex();
}
