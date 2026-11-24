using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Kind of an ASS event.
/// </summary>
public enum AssEventKind
{
    /// <summary>Spoken dialogue.</summary>
    Dialogue,

    /// <summary>Song lyrics (opening/ending/insert songs, karaoke).</summary>
    Song,

    /// <summary>Typeset signs, titles, notes.</summary>
    Sign,
}

/// <summary>
/// Converts ASS/SSA subtitles (anime typesetting) into plain cues: dialogue (+ songs) for a clean SRT that follows the
/// uniform style, and signs + songs for a "forced" track (what viewers of a dub still need).
/// </summary>
public static partial class AssConverter
{
    /// <summary>
    /// Parses the [Events] section.
    /// </summary>
    /// <param name="ass">ASS file content.</param>
    /// <returns>Events with kind and plain text.</returns>
    public static List<(SubtitleCue Cue, AssEventKind Kind)> Parse(string ass)
    {
        var result = new List<(SubtitleCue, AssEventKind)>();
        string[]? format = null;
        var inEvents = false;
        foreach (var raw in ass.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = line[7..].Split(',').Select(f => f.Trim()).ToArray();
                continue;
            }

            if (format is null || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Comment: lines etc.
            }

            var fields = line[9..].Split(',', format.Length);
            if (fields.Length < format.Length)
            {
                continue;
            }

            string Field(string name)
            {
                var i = Array.FindIndex(format, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? fields[i].Trim() : string.Empty;
            }

            var text = fields[^1];
            if (!TryTime(Field("Start"), out var start) || !TryTime(Field("End"), out var end) || end <= start)
            {
                continue;
            }

            var kind = Classify(Field("Style"), Field("Name"), Field("Effect"), text);
            if (DrawingRegex().IsMatch(text))
            {
                continue; // vector drawings (\p1): shapes, no text
            }

            // Keep italics: {\i1}…{\i0} → <i>…</i>; all other override blocks are dropped by NormalizeText.
            var withItalics = OverrideBlockRegex().Replace(text, m => m.Value.Contains("\\i1", StringComparison.Ordinal) ? "<i>" : m.Value.Contains("\\i0", StringComparison.Ordinal) ? "</i>" : m.Value);
            var plain = Srt.NormalizeText(withItalics);
            if (plain.Length == 0)
            {
                continue;
            }

            result.Add((new SubtitleCue(start, end, plain), kind));
        }

        return result;
    }

    /// <summary>
    /// Builds clean cues: events of the given kinds, typesetting layers merged (same text shown in several layers or
    /// split into many frame-long pieces becomes one cue).
    /// </summary>
    /// <param name="events">Parsed events.</param>
    /// <param name="kinds">Kinds to keep.</param>
    /// <returns>Cues ordered by time.</returns>
    public static List<SubtitleCue> Build(IEnumerable<(SubtitleCue Cue, AssEventKind Kind)> events, params AssEventKind[] kinds)
    {
        var merged = new List<SubtitleCue>();
        foreach (var cue in events.Where(e => kinds.Contains(e.Kind)).Select(e => e.Cue).OrderBy(c => c.StartMs).ThenBy(c => c.EndMs))
        {
            // Same text overlapping or directly following (≤ 100 ms): extend instead of duplicating.
            var idx = merged.FindLastIndex(m => m.Text == cue.Text && cue.StartMs <= m.EndMs + 100);
            if (idx >= 0)
            {
                merged[idx] = merged[idx] with { EndMs = Math.Max(merged[idx].EndMs, cue.EndMs) };
                continue;
            }

            merged.Add(cue);
        }

        // Typesetting fragments shorter than a quarter second are not readable as subtitles.
        return merged.Where(c => c.EndMs - c.StartMs >= 250).ToList();
    }

    /// <summary>
    /// Classifies an event by style/actor name and override tags.
    /// </summary>
    /// <param name="style">Style name.</param>
    /// <param name="actor">Actor/name field.</param>
    /// <param name="effect">Effect field.</param>
    /// <param name="text">Raw text with override tags.</param>
    /// <returns>Kind.</returns>
    public static AssEventKind Classify(string style, string actor, string effect, string text)
    {
        var name = style + " " + actor;
        if (SongNameRegex().IsMatch(name) || KaraokeRegex().IsMatch(text) || effect.Contains("karaoke", StringComparison.OrdinalIgnoreCase))
        {
            return AssEventKind.Song;
        }

        if (SignNameRegex().IsMatch(name) || PositionRegex().IsMatch(text))
        {
            return AssEventKind.Sign;
        }

        return AssEventKind.Dialogue;
    }

    private static bool TryTime(string s, out long ms)
    {
        // H:MM:SS.cc
        ms = 0;
        var p = s.Split(':', '.');
        if (p.Length != 4 || !int.TryParse(p[0], CultureInfo.InvariantCulture, out var h) || !int.TryParse(p[1], CultureInfo.InvariantCulture, out var m)
            || !int.TryParse(p[2], CultureInfo.InvariantCulture, out var sec) || !int.TryParse(p[3], CultureInfo.InvariantCulture, out var frac))
        {
            return false;
        }

        ms = (((h * 60L) + m) * 60 + sec) * 1000 + (p[3].Length == 2 ? frac * 10 : p[3].Length == 1 ? frac * 100 : frac);
        return true;
    }

    [GeneratedRegex(@"\b(op|ed|opening|ending|song|lyrics?|kara(oke)?|insert|romaji|kanji|eng?[- _]?song)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SongNameRegex();

    [GeneratedRegex(@"\b(sign|signs|title|typeset|ts|note|notes|caption|logo|episode|ep[- _]?title|card|eyecatch|preview|next)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignNameRegex();

    [GeneratedRegex(@"\\(k|kf|ko|K)\d")]
    private static partial Regex KaraokeRegex();

    [GeneratedRegex(@"\\(pos|move|org|clip|iclip)\(")]
    private static partial Regex PositionRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideBlockRegex();

    [GeneratedRegex(@"\\p[1-9]")]
    private static partial Regex DrawingRegex();
}
