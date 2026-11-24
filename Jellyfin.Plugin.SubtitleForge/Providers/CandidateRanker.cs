using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Scores subtitle candidates against the local file. A "safe" match is one whose timing can be trusted
/// without audio verification: same file hash, or same release group and source.
/// </summary>
public static class CandidateRanker
{
    /// <summary>
    /// Scores and sorts candidates (best first).
    /// </summary>
    /// <param name="candidates">Candidates.</param>
    /// <param name="query">Query (with parsed release of the local file).</param>
    /// <param name="preferHearingImpaired">Whether HI subtitles are preferred.</param>
    /// <param name="sourceBonus">Learned points per source (see <see cref="SourceStats"/>).</param>
    /// <returns>Sorted list.</returns>
    public static List<SubtitleCandidate> Rank(IEnumerable<SubtitleCandidate> candidates, SubtitleQuery query, bool preferHearingImpaired = false, IReadOnlyDictionary<string, int>? sourceBonus = null)
    {
        var file = query.Release;
        var list = new List<SubtitleCandidate>();
        foreach (var c in candidates)
        {
            c.Score = 0;
            c.ScoreReasons.Clear();
            c.IsSafeMatch = false;

            if (c.HashMatch)
            {
                Add(c, 1000, "hash");
                c.IsSafeMatch = true;
            }

            if (c.MachineTranslated)
            {
                Add(c, -500, "machine-translated");
            }

            if (query.IsEpisode)
            {
                if (c.Season.HasValue && query.Season.HasValue && c.Season != query.Season && c.Season != 0)
                {
                    continue; // wrong season
                }

                if (c.Episode.HasValue && query.Episode.HasValue && c.Episode != 0 && c.Episode != query.Episode && !c.IsPack)
                {
                    continue; // wrong episode
                }

                // Sources sometimes omit the episode number; the release name still tells (e.g. "S01E33").
                if (!c.IsPack && query.Episode.HasValue && c.Releases.Count > 0)
                {
                    var named = c.Releases.Select(ReleaseInfo.ParseEpisode).Where(e => e.Episode.HasValue).ToList();
                    if (named.Count > 0 && named.All(e => e.Episode != query.Episode || (e.Season.HasValue && query.Season.HasValue && e.Season != query.Season)))
                    {
                        continue;
                    }
                }

                if (c.IsPack)
                {
                    Add(c, -20, "pack");
                }
                else if (c.Episode == query.Episode)
                {
                    Add(c, 50, "episode");
                }
            }

            if (file is not null && c.Releases.Count > 0)
            {
                var best = c.Releases.Select(r => (Release: ReleaseInfo.Parse(r), Name: r))
                    .Select(r => (r.Name, Points: ReleasePoints(file, r.Release, out var safe, out var why), Safe: safe, Why: why))
                    .OrderByDescending(r => r.Points)
                    .First();
                c.Score += best.Points;
                c.ScoreReasons.AddRange(best.Why);
                c.IsSafeMatch |= best.Safe;
            }

            if (c.HearingImpaired != preferHearingImpaired)
            {
                Add(c, -15, c.HearingImpaired ? "hearing-impaired" : "not-hi");
            }

            if (c.Downloads is > 0)
            {
                Add(c, (int)Math.Min(30, Math.Log10(c.Downloads.Value + 1) * 8), "popular");
            }

            if (sourceBonus is not null && sourceBonus.TryGetValue(c.Source, out var bonus) && bonus != 0)
            {
                Add(c, Math.Clamp(bonus, -SourceStats.MaxBonus, SourceStats.MaxBonus), "source-history");
            }

            list.Add(c);
        }

        return list.OrderByDescending(c => c.Score).ThenByDescending(c => c.Downloads ?? 0).ToList();
    }

    private static int ReleasePoints(ReleaseInfo file, ReleaseInfo sub, out bool safe, out List<string> why)
    {
        var points = 0;
        why = new List<string>();
        var groupMatch = file.Group is not null && file.Group == sub.Group;
        var sourceCompatible = file.SourceType is null || sub.SourceType is null || file.SourceType == sub.SourceType;

        if (groupMatch)
        {
            points += 300;
            why.Add("group:" + file.Group);
        }

        if (file.SourceType is not null && sub.SourceType is not null)
        {
            if (file.SourceType == sub.SourceType)
            {
                points += 60;
                why.Add("source:" + file.SourceType);
            }
            else
            {
                points -= 60;
                why.Add("source-mismatch");
            }
        }

        if (file.Service is not null && file.Service == sub.Service)
        {
            points += 40;
            why.Add("service:" + file.Service);
        }

        if (file.Resolution is not null && file.Resolution == sub.Resolution)
        {
            points += 15;
            why.Add("res:" + file.Resolution);
        }

        // Editions differ in runtime → a mismatch means guaranteed desync somewhere.
        var fileEd = new HashSet<string>(file.EditionTags);
        var subEd = new HashSet<string>(sub.EditionTags);
        if (!fileEd.SetEquals(subEd))
        {
            points -= 100;
            why.Add("edition-mismatch");
        }

        safe = groupMatch && sourceCompatible && fileEd.SetEquals(subEd);
        return points;
    }

    private static void Add(SubtitleCandidate c, int points, string reason)
    {
        c.Score += points;
        c.ScoreReasons.Add(reason);
    }
}
