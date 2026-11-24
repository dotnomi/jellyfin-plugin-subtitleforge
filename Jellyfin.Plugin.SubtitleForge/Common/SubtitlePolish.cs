using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubtitleForge.Configuration;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Which polish steps run on written subtitles.
/// </summary>
/// <param name="FixEncoding">Repair double-encoded UTF-8 and remove control characters.</param>
/// <param name="WrapLines">Re-break long lines.</param>
/// <param name="MaxLineLength">Longest line (visible characters) when wrapping.</param>
/// <param name="MinDuration">Extend cues that are too short to read.</param>
public sealed record PolishOptions(bool FixEncoding, bool WrapLines, int MaxLineLength, bool MinDuration)
{
    /// <summary>Gets options with every step off.</summary>
    public static PolishOptions None { get; } = new(false, false, SubtitlePolish.DefaultMaxLineLength, false);

    /// <summary>Gets a value indicating whether any step runs.</summary>
    public bool Any => FixEncoding || WrapLines || MinDuration;

    /// <summary>
    /// Reads the options from the plugin configuration.
    /// </summary>
    /// <param name="config">Configuration (null = nothing runs).</param>
    /// <returns>Options.</returns>
    public static PolishOptions FromConfig(PluginConfiguration? config) => config is null
        ? None
        : new(config.PolishFixEncoding, config.PolishWrapLines, config.PolishMaxLineLength, config.PolishMinDuration);
}

/// <summary>
/// Final clean-up of cues before they are written: repairs broken encodings, re-breaks long lines and
/// keeps cues on screen long enough to read. All functions are pure.
/// </summary>
public static partial class SubtitlePolish
{
    /// <summary>Default longest line.</summary>
    public const int DefaultMaxLineLength = 42;

    /// <summary>Shortest display time in ms.</summary>
    public const long MinDisplayMs = 1000;

    /// <summary>Display time per visible character in ms.</summary>
    public const long MsPerChar = 50;

    /// <summary>Longest display time the extension aims for in ms.</summary>
    public const long MaxDisplayMs = 7000;

    /// <summary>Gap kept before the next cue in ms.</summary>
    public const long GapMs = 80;

    // CP1252 characters for the bytes 0x80-0x9F (the rest of 0x80-0xFF is Latin-1).
    private static readonly Dictionary<char, byte> Cp1252High = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
        ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
        ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Runs the enabled steps.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <param name="options">Options.</param>
    /// <returns>Polished cues (same order).</returns>
    public static List<SubtitleCue> Apply(List<SubtitleCue> cues, PolishOptions options)
    {
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(options);
        IEnumerable<SubtitleCue> result = cues;
        if (options.FixEncoding)
        {
            result = result.Select(c => c with { Text = FixMojibake(c.Text) });
        }

        if (options.WrapLines)
        {
            result = result.Select(c => c with { Text = WrapLines(c.Text, options.MaxLineLength) });
        }

        var list = result.ToList();
        return options.MinDuration ? ExtendShortDurations(list) : list;
    }

    /// <summary>
    /// Repairs UTF-8 text that was read as Latin-1/CP1252 ("Ã¤" → "ä", "â€™" → "’"), removes U+FFFD and
    /// control characters (except '\n'). Text without such sequences is left as it is.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <returns>Repaired text.</returns>
    public static string FixMojibake(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Twice for text that was broken twice ("ÃƒÂ¤").
        for (var pass = 0; pass < 2; pass++)
        {
            var repaired = RepairSequences(text);
            if (ReferenceEquals(repaired, text))
            {
                break;
            }

            text = repaired;
        }

        if (!NeedsStripping(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (ch is '\t' or '\r')
            {
                sb.Append(ch == '\t' ? ' ' : '\n');
            }
            else if (ch == '\n' || !IsStripped(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Re-breaks a cue so no line is longer than <paramref name="maxLineLength"/> visible characters.
    /// Two lines are preferred, three are used if unavoidable. Dialogue lines ("- …") stay on their own line,
    /// italic tags stay valid on every line. Cues that already fit in at most two lines are not changed.
    /// </summary>
    /// <param name="text">Text (lines separated by '\n').</param>
    /// <param name="maxLineLength">Longest line.</param>
    /// <returns>Wrapped text.</returns>
    public static string WrapLines(string text, int maxLineLength = DefaultMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        maxLineLength = Math.Max(10, maxLineLength);
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0 || (lines.Count <= 2 && lines.All(l => VisibleLength(l) <= maxLineLength)))
        {
            return text;
        }

        // Blocks: each dialogue line with its continuation lines, or all lines of a cue without dialogue.
        var blocks = new List<List<string>>();
        foreach (var line in lines)
        {
            if (blocks.Count == 0 || IsDialogue(line))
            {
                blocks.Add(new List<string>());
            }

            blocks[^1].Add(line.Trim());
        }

        var output = new List<string>();
        foreach (var block in blocks)
        {
            var words = MergeItalics(string.Join(' ', block)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            output.AddRange(FixItalics(BreakWords(words, maxLineLength)));
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// Keeps cues on screen long enough to read: max(1 s, 50 ms per character), at most 7 s. Only the end moves,
    /// and only into free time up to 80 ms before the next cue; cues are never shortened and never overlap.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <returns>Cues in the same order.</returns>
    public static List<SubtitleCue> ExtendShortDurations(List<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var result = new List<SubtitleCue>(cues);
        var order = Enumerable.Range(0, cues.Count).OrderBy(i => cues[i].StartMs).ToArray();
        for (var k = 0; k < order.Length; k++)
        {
            var cue = cues[order[k]];
            var chars = VisibleLength(cue.Text.Replace("\n", string.Empty, StringComparison.Ordinal));
            var target = Math.Min(MaxDisplayMs, Math.Max(MinDisplayMs, chars * MsPerChar));
            if (cue.EndMs - cue.StartMs >= target)
            {
                continue;
            }

            var end = cue.StartMs + target;
            if (k + 1 < order.Length)
            {
                end = Math.Min(end, cues[order[k + 1]].StartMs - GapMs);
            }

            if (end > cue.EndMs)
            {
                result[order[k]] = cue with { EndMs = end };
            }
        }

        return result;
    }

    private static string RepairSequences(string text)
    {
        StringBuilder? sb = null;
        var i = 0;
        var copied = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var need = c switch
            {
                >= 'Â' and <= 'ß' => 1,
                >= 'à' and <= 'ï' => 2,
                >= 'ð' and <= 'ô' => 3,
                _ => 0,
            };
            if (need == 0 || i + need >= text.Length)
            {
                i++;
                continue;
            }

            var bytes = new byte[need + 1];
            bytes[0] = (byte)c;
            var ok = true;
            for (var j = 1; j <= need; j++)
            {
                if (!TryContinuation(text[i + j], out bytes[j]))
                {
                    ok = false;
                    break;
                }
            }

            string decoded;
            try
            {
                decoded = ok ? StrictUtf8.GetString(bytes) : string.Empty;
            }
            catch (DecoderFallbackException)
            {
                decoded = string.Empty;
            }

            if (decoded.Length == 0)
            {
                i++;
                continue;
            }

            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copied, i - copied).Append(decoded);
            i += need + 1;
            copied = i;
        }

        return sb is null ? text : sb.Append(text, copied, text.Length - copied).ToString();
    }

    private static bool TryContinuation(char c, out byte b)
    {
        if (c is >= '\u0080' and <= '¿')
        {
            b = (byte)c;
            return true;
        }

        if (Cp1252High.TryGetValue(c, out b))
        {
            return true;
        }

        b = 0;
        return false;
    }

    private static bool IsStripped(char c) => c == '�' || (char.IsControl(c) && c != '\n');

    private static bool NeedsStripping(string text)
    {
        foreach (var c in text)
        {
            if (IsStripped(c))
            {
                return true;
            }
        }

        return false;
    }

    private static int VisibleLength(string s) => TagRegex().Replace(s, string.Empty).Trim().Length;

    private static bool IsDialogue(string line)
    {
        var bare = TagRegex().Replace(line, string.Empty).TrimStart();
        return bare.Length > 1 && bare[0] == '-' && (bare[1] == ' ' || char.IsLetter(bare[1]));
    }

    // "<i>a</i> <i>b</i>" (from joined italic lines) → "<i>a b</i>".
    private static string MergeItalics(string s) => ItalicGapRegex().Replace(s, " ");

    private static List<string> BreakWords(string[] words, int max)
    {
        var lens = words.Select(w => VisibleLength(w)).ToArray();
        int Len(int from, int to)
        {
            var n = 0;
            for (var i = from; i < to; i++)
            {
                n += lens[i] + (i > from ? 1 : 0);
            }

            return n;
        }

        string Join(int from, int to) => string.Join(' ', words[from..to]);

        var count = words.Length;
        if (count <= 1 || Len(0, count) <= max)
        {
            return new List<string> { Join(0, count) };
        }

        // Two lines: the break that fits and is most balanced.
        var best = -1;
        var bestScore = int.MaxValue;
        for (var b = 1; b < count; b++)
        {
            var score = Math.Max(Len(0, b), Len(b, count));
            if (score <= max && score < bestScore)
            {
                (best, bestScore) = (b, score);
            }
        }

        if (best > 0)
        {
            return new List<string> { Join(0, best), Join(best, count) };
        }

        // Three lines if two do not fit.
        var best3 = (A: -1, B: -1);
        bestScore = int.MaxValue;
        for (var a = 1; a < count - 1; a++)
        {
            for (var b = a + 1; b < count; b++)
            {
                var score = Math.Max(Len(0, a), Math.Max(Len(a, b), Len(b, count)));
                if (score <= max && score < bestScore)
                {
                    (best3, bestScore) = ((a, b), score);
                }
            }
        }

        if (best3.A > 0)
        {
            return new List<string> { Join(0, best3.A), Join(best3.A, best3.B), Join(best3.B, count) };
        }

        // Nothing fits (very long text or words): greedy.
        var result = new List<string>();
        var start = 0;
        for (var i = 1; i <= count; i++)
        {
            if (i == count || Len(start, i + 1) > max)
            {
                result.Add(Join(start, i));
                start = i;
            }
        }

        return result;
    }

    // Closes italics at the end of a line and reopens them on the next one.
    private static List<string> FixItalics(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var open = false;
        foreach (var line in lines)
        {
            var l = open ? "<i>" + line : line;
            foreach (Match m in ItalicTagRegex().Matches(line))
            {
                open = m.Value[1] != '/';
            }

            if (open)
            {
                l += "</i>";
            }

            result.Add(l.Replace("<i></i>", string.Empty, StringComparison.Ordinal).Trim());
        }

        return result;
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"</?i>")]
    private static partial Regex ItalicTagRegex();

    [GeneratedRegex(@"</i>\s+<i>")]
    private static partial Regex ItalicGapRegex();
}
