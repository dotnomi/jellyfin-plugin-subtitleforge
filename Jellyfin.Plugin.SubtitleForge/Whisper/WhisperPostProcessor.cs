using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Sync;

namespace Jellyfin.Plugin.SubtitleForge.Whisper;

/// <summary>
/// Turns raw whisper segments into readable subtitles: removes hallucinations, splits into ≤2 lines of ≤42 chars, ≤6 s.
/// </summary>
public static partial class WhisperPostProcessor
{
    /// <summary>Max characters per line.</summary>
    public const int MaxLineChars = 42;

    private const long MaxCueMs = 6000;
    private const long MinCueMs = 700;

    // Phrases whisper is known to invent in silence/music (it was trained on subtitle files with such credits).
    private static readonly string[] Hallucinations =
    {
        "untertitel im auftrag des zdf", "untertitel der amara.org-community", "untertitelung des zdf", "untertitel von stephanie geiges",
        "copyright wdr", "swr 20", "vielen dank fürs zuschauen", "danke fürs zuschauen", "bis zum nächsten mal",
        "thanks for watching", "thank you for watching", "please subscribe", "subtitles by the amara.org community",
        "transcription by castingwords", "www.mooji.org", "ご視聴ありがとうございました", "チャンネル登録",
    };

    /// <summary>
    /// Builds subtitle cues from whisper output.
    /// </summary>
    /// <param name="segments">Segments.</param>
    /// <param name="speech">Speech signal (to drop segments over silence), optional.</param>
    /// <returns>Cues.</returns>
    public static List<SubtitleCue> ToCues(IEnumerable<WhisperSegment> segments, SpeechSignal? speech)
    {
        var cleaned = new List<WhisperSegment>();
        string? last = null;
        var repeat = 0;
        foreach (var s in segments.OrderBy(s => s.StartMs))
        {
            var text = Normalize(s.Text);
            if (text.Length == 0 || IsHallucination(text) || OnlySymbolsRegex().IsMatch(text))
            {
                continue;
            }

            // Repetition loops: the same line over and over.
            if (string.Equals(text, last, StringComparison.OrdinalIgnoreCase))
            {
                if (++repeat >= 2)
                {
                    continue;
                }
            }
            else
            {
                repeat = 0;
            }

            last = text;
            if (speech is not null && SpeechShare(s, speech) < 0.15)
            {
                continue; // text over silence/music is almost always invented
            }

            cleaned.Add(s with { Text = text, EndMs = Math.Max(s.EndMs, s.StartMs + 300) });
        }

        var cues = new List<SubtitleCue>();
        foreach (var s in cleaned)
        {
            cues.AddRange(Split(s));
        }

        // Whisper's timestamps are coarse (±0.5 s). Snap each start to the nearest detected speech onset, with the
        // lead professional subtitles use (calibrated on Blu-ray/WEB references).
        if (speech is not null)
        {
            SnapToOnsets(cues, speech);
        }

        // Minimum display time without overlapping the next cue.
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            var limit = i + 1 < cues.Count ? cues[i + 1].StartMs - 40 : long.MaxValue;
            if (c.EndMs - c.StartMs < MinCueMs)
            {
                cues[i] = c with { EndMs = Math.Min(c.StartMs + MinCueMs, Math.Max(c.EndMs, limit)) };
            }
            else if (c.EndMs > limit)
            {
                cues[i] = c with { EndMs = Math.Max(c.StartMs + 200, limit) };
            }
        }

        return cues;
    }

    /// <summary>
    /// Wraps text into lines of at most <see cref="MaxLineChars"/> characters, balanced.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <returns>Lines.</returns>
    public static List<string> Wrap(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            if (sb.Length > 0 && sb.Length + 1 + w.Length > MaxLineChars)
            {
                lines.Add(sb.ToString());
                sb.Clear();
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(w);
        }

        if (sb.Length > 0)
        {
            lines.Add(sb.ToString());
        }

        // Balance two short lines ("aaaaaaaaaaaaaaaaaaaaa b" → "aaaaaaaaaa" / "aaaaaaaaa b").
        if (lines.Count == 2)
        {
            var all = string.Join(' ', lines);
            var mid = all.Length / 2;
            var left = all.LastIndexOf(' ', mid);
            var right = all.IndexOf(' ', mid);
            var cut = left < 0 ? right : right < 0 ? left : (mid - left <= right - mid ? left : right);
            if (cut > 0 && cut <= MaxLineChars && all.Length - cut - 1 <= MaxLineChars)
            {
                lines = new List<string> { all[..cut], all[(cut + 1)..] };
            }
        }

        return lines;
    }

    private static IEnumerable<SubtitleCue> Split(WhisperSegment s)
    {
        var lines = Wrap(s.Text);
        var duration = s.EndMs - s.StartMs;
        if (lines.Count <= 2 && duration <= MaxCueMs)
        {
            yield return new SubtitleCue(s.StartMs, s.EndMs, string.Join('\n', lines));
            yield break;
        }

        // Several cues: split the words into equally long pieces (by characters), each wrapped to ≤2 lines,
        // and give each piece time proportional to its length. Avoids a lone word as the last cue.
        var pieces = Math.Max((int)Math.Ceiling(lines.Count / 2.0), (int)Math.Ceiling(duration / (double)MaxCueMs));
        var words = s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        pieces = Math.Clamp(pieces, 1, Math.Max(1, words.Length));
        var target = s.Text.Length / (double)pieces;
        var groups = new List<string>();
        var current = new List<string>();
        foreach (var w in words)
        {
            current.Add(w);
            var len = string.Join(' ', current).Length;
            if (groups.Count < pieces - 1 && len >= target)
            {
                groups.Add(string.Join(' ', current));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            groups.Add(string.Join(' ', current));
        }

        var total = Math.Max(1, groups.Sum(g => g.Length));
        var t = s.StartMs;
        for (var i = 0; i < groups.Count; i++)
        {
            var end = i == groups.Count - 1 ? s.EndMs : t + (long)Math.Round(duration * (groups[i].Length / (double)total));
            var wrapped = Wrap(groups[i]);
            if (wrapped.Count > 2)
            {
                // Still too long for two lines: halve once more.
                var mid = t + ((end - t) / 2);
                var half = wrapped.Count / 2;
                yield return new SubtitleCue(t, mid, string.Join('\n', wrapped.Take(half)));
                yield return new SubtitleCue(mid, end, string.Join('\n', wrapped.Skip(half)));
            }
            else
            {
                yield return new SubtitleCue(t, end, string.Join('\n', wrapped));
            }

            t = end;
        }
    }

    /// <summary>
    /// Cuts repetition loops inside one segment ("I hate the word that is a word that ... I hate the word"):
    /// when a phrase of 3+ words occurs a third time, the text is cut before that occurrence.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <returns>Shortened text.</returns>
    public static string CutRepetitions(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var n = 3; n <= 6; n++)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + n <= words.Length; i++)
            {
                var key = string.Join(' ', words, i, n).Trim('.', ',', '!', '?', '"', '\'');
                seen[key] = seen.TryGetValue(key, out var c) ? c + 1 : 1;
                if (seen[key] >= 3)
                {
                    var cut = string.Join(' ', words, 0, i).TrimEnd(',', ' ');
                    return cut.Length > 0 ? cut + " …" : text;
                }
            }
        }

        return text;
    }

    private static void SnapToOnsets(List<SubtitleCue> cues, SpeechSignal speech)
    {
        const long Window = 600;
        const long Lead = 150;
        var onsets = new List<long>();
        for (var t = 1; t < speech.Length; t++)
        {
            if (speech.Frames[t] >= 128 && speech.Frames[t - 1] < 128)
            {
                onsets.Add(t * (long)SpeechSignal.FrameMs);
            }
        }

        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            var idx = onsets.BinarySearch(c.StartMs);
            if (idx < 0)
            {
                idx = ~idx;
            }

            long? best = null;
            foreach (var k in new[] { idx - 1, idx })
            {
                if (k >= 0 && k < onsets.Count && Math.Abs(onsets[k] - c.StartMs) <= Window
                    && (best is null || Math.Abs(onsets[k] - c.StartMs) < Math.Abs(best.Value - c.StartMs)))
                {
                    best = onsets[k];
                }
            }

            if (best is null)
            {
                continue;
            }

            var previousEnd = i > 0 ? cues[i - 1].EndMs + 40 : 0;
            var start = Math.Max(previousEnd, best.Value - Lead);
            if (start < c.EndMs - 300)
            {
                cues[i] = c with { StartMs = start };
            }
        }
    }

    private static string Normalize(string text)
    {
        var t = text.Replace("\n", " ", StringComparison.Ordinal).Trim();
        t = CutRepetitions(t);
        t = BracketNoiseRegex().Replace(t, string.Empty); // "[Musik]", "(applause)", "*seufzt*"
        t = MultiSpaceRegex().Replace(t, " ").Trim();
        return t.Trim('-', ' ');
    }

    private static bool IsHallucination(string text)
    {
        var lower = text.ToLowerInvariant();
        return Hallucinations.Any(h => lower.Contains(h, StringComparison.Ordinal));
    }

    private static double SpeechShare(WhisperSegment s, SpeechSignal speech)
    {
        var a = Math.Max(0, s.StartMs / SpeechSignal.FrameMs);
        var b = Math.Min(speech.Length, s.EndMs / SpeechSignal.FrameMs);
        if (b <= a)
        {
            return 0;
        }

        var on = 0;
        for (var t = a; t < b; t++)
        {
            if (speech.Frames[t] >= 100)
            {
                on++;
            }
        }

        return (double)on / (b - a);
    }

    [GeneratedRegex(@"^[\p{P}\p{S}\s♪]+$")]
    private static partial Regex OnlySymbolsRegex();

    [GeneratedRegex(@"[\[\(\*][^\]\)\*]{1,40}[\]\)\*]")]
    private static partial Regex BracketNoiseRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();
}
