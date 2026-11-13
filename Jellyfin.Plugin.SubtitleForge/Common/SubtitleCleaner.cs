using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Removes debris that automatic ASS→SRT conversions leave behind (animated typesetting split into dozens of
/// 1-character cues per frame). Such cues are unreadable on screen and confuse timing analysis.
/// </summary>
public static class SubtitleCleaner
{
    private const long MinDurationMs = 250;
    private const int MaxSameTimestamp = 3;

    /// <summary>
    /// Cleans a cue list.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <returns>Cleaned cues.</returns>
    public static List<SubtitleCue> Clean(IEnumerable<SubtitleCue> cues)
    {
        var list = cues.ToList();
        var crowded = list.GroupBy(c => (c.StartMs, c.EndMs)).Where(g => g.Count() > MaxSameTimestamp).Select(g => g.Key).ToHashSet();
        return list.Where(c =>
                c.EndMs - c.StartMs >= MinDurationMs
                && !crowded.Contains((c.StartMs, c.EndMs))
                && Srt.NormalizeText(c.Text).Replace("<i>", string.Empty, System.StringComparison.Ordinal).Replace("</i>", string.Empty, System.StringComparison.Ordinal).Trim().Length > 1)
            .ToList();
    }
}
