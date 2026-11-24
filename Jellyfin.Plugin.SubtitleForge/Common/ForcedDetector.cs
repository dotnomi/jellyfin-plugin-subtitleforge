using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubtitleForge.Sync;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Detects "forced" subtitles (only signs/foreign-language parts) that sources do not flag as such.
/// A full subtitle covers most of the dialogue; a forced one covers only a small fraction.
/// </summary>
public static partial class ForcedDetector
{
    /// <summary>Below this share of speech covered by subtitles, a subtitle is considered forced.</summary>
    public const double MaxForcedCoverage = 0.25;

    /// <summary>
    /// Among several tracks of the same language, the ones with only a small fraction of the cues of the largest
    /// track (signs/songs only) are forced.
    /// </summary>
    /// <typeparam name="T">Track type.</typeparam>
    /// <param name="tracks">Track, language, number of cues.</param>
    /// <returns>Tracks to treat as forced.</returns>
    public static IEnumerable<T> SmallTracks<T>(IEnumerable<(T Track, string Language, int Cues)> tracks)
    {
        foreach (var group in tracks.GroupBy(t => t.Language))
        {
            var list = group.ToList();
            if (list.Count < 2)
            {
                continue;
            }

            var max = list.Max(t => t.Cues);
            foreach (var t in list.Where(t => t.Cues < max * MaxForcedCoverage && t.Cues < 200))
            {
                yield return t.Track;
            }
        }
    }

    /// <summary>
    /// Whether a release name says it is forced.
    /// </summary>
    /// <param name="releases">Release names.</param>
    /// <returns>True if marked.</returns>
    public static bool NameSaysForced(IEnumerable<string> releases) => releases.Any(r => ForcedNameRegex().IsMatch(r));

    /// <summary>
    /// Share of detected speech that lies inside a subtitle.
    /// </summary>
    /// <param name="cues">Cues (aligned).</param>
    /// <param name="speech">Speech signal.</param>
    /// <returns>0..1.</returns>
    public static double SpeechCoverage(IReadOnlyList<SubtitleCue> cues, SpeechSignal speech)
    {
        var covered = new bool[speech.Length];
        foreach (var c in cues)
        {
            var a = (int)Math.Clamp(c.StartMs / SpeechSignal.FrameMs, 0, speech.Length);
            var b = (int)Math.Clamp(c.EndMs / SpeechSignal.FrameMs, 0, speech.Length);
            for (var t = a; t < b; t++)
            {
                covered[t] = true;
            }
        }

        long speechFrames = 0, hit = 0;
        for (var t = 0; t < speech.Length; t++)
        {
            if (speech.Frames[t] >= 128)
            {
                speechFrames++;
                if (covered[t])
                {
                    hit++;
                }
            }
        }

        return speechFrames == 0 ? 1 : (double)hit / speechFrames;
    }

    /// <summary>
    /// Decides whether a subtitle is forced-only.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <param name="speech">Speech signal, if available.</param>
    /// <param name="runtimeMs">Runtime of the video (fallback when no speech signal).</param>
    /// <returns>True if likely forced.</returns>
    public static bool IsLikelyForced(IReadOnlyList<SubtitleCue> cues, SpeechSignal? speech, long runtimeMs)
    {
        if (speech is not null && speech.SpeechRatio() >= 0.03)
        {
            return SpeechCoverage(cues, speech) < MaxForcedCoverage;
        }

        // Without audio analysis: full subtitles have several cues per minute of runtime.
        return runtimeMs > 10 * 60_000 && cues.Count < runtimeMs / 60_000.0 * 1.5;
    }

    [GeneratedRegex(@"(?i)(?<![a-z])(forced|foreign[ ._-]?(parts|only)?|nur[ ._-]?fremdsprachig)(?![a-z])")]
    private static partial Regex ForcedNameRegex();
}
