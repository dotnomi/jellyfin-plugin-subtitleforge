using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Jellyfin.Plugin.SubtitleForge.Common;

namespace Jellyfin.Plugin.SubtitleForge.Sync;

/// <summary>
/// Result of aligning subtitles to the speech signal.
/// </summary>
public sealed class AlignmentResult
{
    /// <summary>Gets or sets the corrected cues.</summary>
    public List<SubtitleCue> Cues { get; set; } = new();

    /// <summary>Gets or sets the applied time scale (framerate correction), 1 = none.</summary>
    public double Ratio { get; set; } = 1;

    /// <summary>Gets or sets the global offset in ms.</summary>
    public long OffsetMs { get; set; }

    /// <summary>Gets or sets the number of segments with distinct offsets (1 = single offset).</summary>
    public int Segments { get; set; } = 1;

    /// <summary>Gets or sets the agreement of the original timing (-1..1).</summary>
    public double ScoreBefore { get; set; }

    /// <summary>Gets or sets the agreement after correction (-1..1).</summary>
    public double ScoreAfter { get; set; }

    /// <summary>Gets or sets how clearly the best offset stands out from all others (z-score).</summary>
    public double Prominence { get; set; }

    /// <summary>Gets or sets the share of subtitle time that overlaps detected speech after correction.</summary>
    public double SpeechOverlap { get; set; }

    /// <summary>Gets or sets the share of subtitle time overlapping speech before correction.</summary>
    public double SpeechOverlapBefore { get; set; }

    /// <summary>Gets or sets the maximum absolute change applied to any cue in ms.</summary>
    public long MaxShiftMs { get; set; }

    /// <summary>
    /// Gets a value indicating whether the subtitle is trustworthy after correction.
    /// </summary>
    public bool IsConfident => Prominence >= SubtitleAligner.MinProminence && SpeechOverlap >= SubtitleAligner.MinSpeechOverlap;

    /// <inheritdoc />
    public override string ToString() =>
        FormattableString.Invariant($"offset {OffsetMs} ms, ratio {Ratio:F4}, {Segments} segment(s), overlap {SpeechOverlapBefore:P0}→{SpeechOverlap:P0}, prominence {Prominence:F1}");
}

/// <summary>
/// Aligns subtitle timing to speech: global offset + framerate via FFT cross-correlation (like ffsubsync),
/// then piecewise refinement for cut differences (other intro, ad breaks).
/// Works independent of language (German subtitles on Japanese audio).
/// </summary>
public static class SubtitleAligner
{
    /// <summary>Minimum z-score of the correlation peak.</summary>
    public const double MinProminence = 4.0;

    /// <summary>Minimum share of subtitle time that must overlap speech.</summary>
    public const double MinSpeechOverlap = 0.30;

    private const int Fps = 1000 / SpeechSignal.FrameMs;
    private const int RefineRangeFrames = 30 * Fps;  // ±30 s around the global offset
    private const int ChunkCues = 30;                 // ≈ 4–5 minutes of dialogue per block
    private const int MinChunkCues = 6;
    private const long TargetLeadMs = -180;           // measured on Blu-ray/WEB references: −120…−311 ms
    private const long PlausibleLeadMinMs = -500;
    private const long PlausibleLeadMaxMs = 50;
    private const double ChunkMinZ = 2.5;             // a block's own offset must stand out from the search window
    private const double ChunkMinImprovement = 0.08;  // … and put ≥8 % more of the block's subtitle time onto speech
    private const int SameOffsetFrames = 15;          // ≤150 ms difference between blocks = same offset
    private const int MinSegmentCues = 8;
    private const int ProminenceWindowFrames = 60 * Fps; // compare against offsets within ±60 s

    [ThreadStatic]
    private static double _outside;

    private static readonly double[] Ratios =
    {
        1.0,
        25 / 23.976, 23.976 / 25,
        24 / 23.976, 23.976 / 24,
        25 / 24.0, 24 / 25.0,
    };

    /// <summary>
    /// Aligns cues to speech.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <param name="speech">Speech signal.</param>
    /// <param name="allowSplits">Whether per-segment offsets are allowed.</param>
    /// <returns>Result.</returns>
    public static AlignmentResult Align(IReadOnlyList<SubtitleCue> cues, SpeechSignal speech, bool allowSplits = true)
    {
        var result = new AlignmentResult { Cues = cues.ToList() };
        if (cues.Count == 0 || speech.Length == 0)
        {
            return result;
        }

        // Centre the speech probability on its mean: speech counts positive, silence slightly negative.
        // (With ±1 weighting, films with little dialogue are dominated by "avoid silence" instead of "hit speech".)
        var mean = speech.Frames.Average(b => b / 255.0);
        var v = new double[speech.Length];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (speech.Frames[i] / 255.0) - mean;
        }

        _outside = mean;

        var prefix = new double[v.Length + 1];
        for (var i = 0; i < v.Length; i++)
        {
            prefix[i + 1] = prefix[i] + v[i];
        }

        result.ScoreBefore = Agreement(cues, 1, 0, prefix);
        result.SpeechOverlapBefore = Overlap(cues, speech);

        // 1) Global offset + framerate.
        double bestScore = double.MinValue, bestRatio = 1, prominence = 0;
        long bestOffsetFrames = 0;
        // The audio spectrum is the same for every framerate candidate: compute it once.
        var maxSubFrames = (long)Math.Ceiling(cues.Max(c => c.EndMs) * Ratios.Max() / SpeechSignal.FrameMs) + 2;
        var size = 1;
        while (size < v.Length + maxSubFrames)
        {
            size <<= 1;
        }

        var spectrum = new Complex[size];
        for (var i = 0; i < v.Length; i++)
        {
            spectrum[i] = v[i];
        }

        Fft(spectrum, false);

        foreach (var ratio in Ratios)
        {
            var (lag, score, z) = GlobalOffset(cues, ratio, v, spectrum);
            var adjusted = ratio == 1.0 ? score + 0.02 : score; // prefer no framerate change unless clearly better
            if (adjusted > bestScore)
            {
                bestScore = adjusted;
                bestRatio = ratio;
                bestOffsetFrames = lag;
                prominence = z;
            }
        }

        result.Ratio = bestRatio;
        result.OffsetMs = bestOffsetFrames * SpeechSignal.FrameMs;
        result.Prominence = prominence;

        var shifted = cues.Select(c => new SubtitleCue(
            (long)Math.Round(c.StartMs * bestRatio) + result.OffsetMs,
            (long)Math.Round(c.EndMs * bestRatio) + result.OffsetMs,
            c.Text)).ToList();

        // 2) Per-cue refinement with split penalty, then merge segments that are too short to be trusted.
        var deltas = new int[shifted.Count];
        if (allowSplits && shifted.Count >= 3)
        {
            deltas = RefineByChunks(shifted, prefix);
            MergeShortSegments(shifted, deltas, prefix);
            FineTuneSegments(shifted, deltas, prefix);
        }

        // Confidence per segment: how clearly its offset beats all other offsets in the search window.
        result.Prominence = SegmentProminence(shifted, deltas, prefix);
        result.Segments = 1;
        for (var i = 0; i < shifted.Count; i++)
        {
            if (i > 0 && deltas[i] != deltas[i - 1])
            {
                result.Segments++;
            }

            var d = deltas[i] * SpeechSignal.FrameMs;
            shifted[i] = shifted[i] with { StartMs = shifted[i].StartMs + d, EndMs = shifted[i].EndMs + d };
        }

        // 3) Onset calibration. Overlap maximisation pulls cues towards the middle of speech; professional subtitles
        //    start shortly before speech instead. Set each segment's median lead (start − speech onset) to TargetLeadMs.
        var onsets = Onsets(speech);
        foreach (var (start, end) in SegmentsOf(deltas))
        {
            var (lead, matched) = MedianLead(shifted, start, end, onsets);
            if (matched >= 0.3)
            {
                var adjust = Math.Clamp(TargetLeadMs - lead, -500, 500);
                for (var i = start; i < end; i++)
                {
                    shifted[i] = shifted[i] with { StartMs = shifted[i].StartMs + adjust, EndMs = shifted[i].EndMs + adjust };
                }
            }
        }

        // 4) Never "fix" a subtitle that is already in sync: small single-offset corrections are dropped when the
        //    original timing already has a plausible lead in front of speech.
        var totalShift = cues.Zip(shifted, (a, b) => b.StartMs - a.StartMs).OrderBy(x => x).ElementAt(cues.Count / 2);
        if (result.Segments == 1 && result.Ratio == 1.0 && Math.Abs(totalShift) <= 1000)
        {
            var original = cues.ToList();
            var (lead, matched) = MedianLead(original, 0, original.Count, onsets);
            if (matched >= 0.3 && lead >= PlausibleLeadMinMs && lead <= PlausibleLeadMaxMs)
            {
                shifted = original;
                result.OffsetMs = 0;
            }
        }

        result.Cues = shifted.Select(c => c.StartMs < 0 ? c with { StartMs = 0, EndMs = Math.Max(1, c.EndMs) } : c).ToList();
        result.ScoreAfter = Agreement(result.Cues, 1, 0, prefix);
        result.SpeechOverlap = Overlap(result.Cues, speech);
        result.MaxShiftMs = cues.Zip(result.Cues, (a, b) => Math.Abs(b.StartMs - a.StartMs)).Max();
        return result;
    }

    /// <summary>
    /// Mean agreement of subtitle-on frames with speech (-1..1).
    /// </summary>
    private static double Agreement(IReadOnlyList<SubtitleCue> cues, double ratio, long offsetFrames, double[] prefix)
    {
        double sum = 0;
        long frames = 0;
        foreach (var c in cues)
        {
            var a = (long)Math.Round(c.StartMs * ratio / SpeechSignal.FrameMs) + offsetFrames;
            var b = (long)Math.Round(c.EndMs * ratio / SpeechSignal.FrameMs) + offsetFrames;
            frames += Math.Max(0, b - a);
            sum += RangeSum(prefix, a, b);
        }

        return frames == 0 ? 0 : sum / frames;
    }

    private static double Overlap(IReadOnlyList<SubtitleCue> cues, SpeechSignal speech)
    {
        long on = 0, hit = 0;
        foreach (var c in cues)
        {
            var a = Math.Max(0, c.StartMs / SpeechSignal.FrameMs);
            var b = Math.Min(speech.Length, c.EndMs / SpeechSignal.FrameMs);
            for (var t = a; t < b; t++)
            {
                on++;
                if (speech.Frames[t] >= 128)
                {
                    hit++;
                }
            }

            if (c.EndMs / SpeechSignal.FrameMs > speech.Length)
            {
                on += (c.EndMs / SpeechSignal.FrameMs) - Math.Max(b, a);
            }
        }

        return on == 0 ? 0 : (double)hit / on;
    }

    private static double RangeSum(double[] prefix, long a, long b)
    {
        // Frames outside the audio count as "no speech" (-1), otherwise pushing subtitles out of the film would pay off.
        var n = prefix.Length - 1;
        if (b <= a)
        {
            return 0;
        }

        var lo = Math.Clamp(a, 0, n);
        var hi = Math.Clamp(b, 0, n);
        var inside = hi > lo ? prefix[hi] - prefix[lo] : 0;
        var outside = (b - a) - Math.Max(0, hi - lo);
        return inside - (outside * _outside);
    }

    /// <summary>
    /// Finds the best lag via FFT cross-correlation. Returns lag in frames, normalised score and z-score.
    /// </summary>
    private static (long Lag, double Score, double Z) GlobalOffset(IReadOnlyList<SubtitleCue> cues, double ratio, double[] v, Complex[] spectrum)
    {
        var subEnd = (int)Math.Ceiling(cues.Max(c => c.EndMs) * ratio / SpeechSignal.FrameMs) + 1;
        var s = new double[subEnd];
        long onFrames = 0;
        foreach (var c in cues)
        {
            var a = Math.Max(0, (int)Math.Round(c.StartMs * ratio / SpeechSignal.FrameMs));
            var b = Math.Min(subEnd, (int)Math.Round(c.EndMs * ratio / SpeechSignal.FrameMs));
            for (var t = a; t < b; t++)
            {
                if (s[t] == 0)
                {
                    s[t] = 1;
                    onFrames++;
                }
            }
        }

        if (onFrames == 0)
        {
            return (0, 0, 0);
        }

        var n = spectrum.Length;
        var fa = new Complex[n];
        for (var i = 0; i < s.Length; i++)
        {
            fa[i] = s[i];
        }

        Fft(fa, false);
        for (var i = 0; i < n; i++)
        {
            fa[i] = spectrum[i] * Complex.Conjugate(fa[i]);
        }

        Fft(fa, true);

        // corr[lag] = Σ s[t] v[t+lag]; lags in [-(s.Length-1), v.Length-1], negative lags wrap around.
        double best = double.MinValue, sum = 0, sumSq = 0;
        long bestLag = 0;
        var count = 0;
        // Prefix count of subtitle frames, to know how many fall outside the audio at a given lag.
        var sPrefix = new long[s.Length + 1];
        for (var i = 0; i < s.Length; i++)
        {
            sPrefix[i + 1] = sPrefix[i] + (s[i] > 0 ? 1 : 0);
        }

        // Only consider lags that keep at least 90 % of the subtitle frames inside the audio.
        var minLag = -(s.Length - 1);
        for (long lag = minLag; lag < v.Length; lag++)
        {
            var idx = lag >= 0 ? lag : n + lag;
            var from = (int)Math.Clamp(-lag, 0, s.Length);
            var to = (int)Math.Clamp(v.Length - lag, 0, s.Length);
            var insideFrames = to > from ? sPrefix[to] - sPrefix[from] : 0;
            if (insideFrames < onFrames * 0.9)
            {
                continue;
            }

            var val = (fa[idx].Real - ((onFrames - insideFrames) * _outside)) / onFrames;
            sum += val;
            sumSq += val * val;
            count++;
            if (val > best)
            {
                best = val;
                bestLag = lag;
            }
        }

        if (count == 0)
        {
            return (0, double.MinValue / 2, 0);
        }

        var mean = sum / count;
        var std = Math.Sqrt(Math.Max(1e-12, (sumSq / count) - (mean * mean)));
        return (bestLag, best, (best - mean) / std);
    }

    /// <summary>
    /// Piecewise refinement: the local offset of each block of ~30 cues is estimated independently and only accepted when
    /// it clearly stands out (z-score). Blocks are smoothed with a median filter, then the exact cue where the offset changes
    /// is searched between two blocks. Robust against sparse dialogue, music and effects.
    /// </summary>
    private static int[] RefineByChunks(List<SubtitleCue> cues, double[] prefix)
    {
        var n = cues.Count;
        var deltas = new int[n];
        var chunkCues = Math.Clamp(n / 6, MinChunkCues, ChunkCues);
        if (n < 2 * chunkCues)
        {
            return deltas;
        }

        var chunks = new List<(int Start, int End)>();
        for (var i = 0; i < n; i += chunkCues)
        {
            var end = Math.Min(n, i + chunkCues);
            if (n - end < chunkCues / 2)
            {
                end = n; // fold a small remainder into the last block
            }

            chunks.Add((i, end));
            if (end == n)
            {
                break;
            }
        }

        // Local offset per block, only if confident.
        var offsets = new int?[chunks.Count];
        for (var c = 0; c < chunks.Count; c++)
        {
            var (start, end) = chunks[c];
            double best = double.MinValue, sum = 0, sumSq = 0;
            var bestD = 0;
            var count = 0;
            for (var d = -RefineRangeFrames; d <= RefineRangeFrames; d += 2)
            {
                var g = SegmentGain(cues, start, end, d, prefix);
                sum += g;
                sumSq += g * g;
                count++;
                if (g > best)
                {
                    best = g;
                    bestD = d;
                }
            }

            var m = sum / count;
            var sd = Math.Sqrt(Math.Max(1e-9, (sumSq / count) - (m * m)));
            var z = (best - m) / sd;
            if (Math.Abs(bestD) <= SameOffsetFrames)
            {
                offsets[c] = 0; // confirms the global offset
            }
            else
            {
                // A different offset must raise the share of subtitle time on speech noticeably and stand out.
                long frames = 0;
                for (var i = start; i < end; i++)
                {
                    frames += Math.Max(0, (cues[i].EndMs - cues[i].StartMs) / SpeechSignal.FrameMs);
                }

                var improvement = (best - SegmentGain(cues, start, end, 0, prefix)) / Math.Max(1, frames);
                if (improvement < ChunkMinImprovement)
                {
                    offsets[c] = 0; // the global offset is as good: keep it
                }
                else if (z >= ChunkMinZ)
                {
                    offsets[c] = bestD;
                }
            }
        }

        // Fill unsure blocks from neighbours (default: global offset).
        var filled = new int[chunks.Count];
        int? last = null;
        for (var c = 0; c < chunks.Count; c++)
        {
            last = offsets[c] ?? last;
            filled[c] = last ?? 0;
        }

        for (var c = chunks.Count - 1; c >= 0 && offsets.Take(c + 1).All(o => o is null); c--)
        {
            filled[c] = offsets.FirstOrDefault(o => o is not null) ?? 0;
        }

        // Median filter (window 3, shifted inwards at the edges) removes single-block outliers, also the first/last block.
        var smooth = new int[chunks.Count];
        for (var c = 0; c < chunks.Count; c++)
        {
            if (chunks.Count < 3)
            {
                smooth[c] = filled[c];
                continue;
            }

            var mid = Math.Clamp(c, 1, chunks.Count - 2);
            var w = new[] { filled[mid - 1], filled[mid], filled[mid + 1] };
            Array.Sort(w);
            smooth[c] = (c == mid || Math.Abs(filled[c] - w[1]) > SameOffsetFrames) ? w[1] : filled[c];
        }

        // Treat near-identical neighbours as the same offset.
        for (var c = 1; c < chunks.Count; c++)
        {
            if (Math.Abs(smooth[c] - smooth[c - 1]) <= SameOffsetFrames)
            {
                smooth[c] = smooth[c - 1];
            }
        }

        for (var c = 0; c < chunks.Count; c++)
        {
            for (var i = chunks[c].Start; i < chunks[c].End; i++)
            {
                deltas[i] = smooth[c];
            }
        }

        // Exact split position between two blocks with different offsets.
        for (var c = 1; c < chunks.Count; c++)
        {
            var left = smooth[c - 1];
            var right = smooth[c];
            if (left == right)
            {
                continue;
            }

            var from = chunks[c - 1].Start;
            var to = chunks[c].End;
            double best = double.MinValue;
            var bestK = chunks[c].Start;
            for (var k = from; k <= to; k++)
            {
                var g = SegmentGain(cues, from, k, left, prefix) + SegmentGain(cues, k, to, right, prefix);
                if (g > best)
                {
                    best = g;
                    bestK = k;
                }
            }

            for (var i = from; i < to; i++)
            {
                deltas[i] = i < bestK ? left : right;
            }
        }

        return deltas;
    }

    /// <summary>
    /// Re-estimates each segment's offset precisely (±1 s, 10 ms steps) using all its cues.
    /// </summary>
    private static void FineTuneSegments(List<SubtitleCue> cues, int[] deltas, double[] prefix)
    {
        foreach (var (start, end) in SegmentsOf(deltas))
        {
            var centre = deltas[start];
            double best = double.MinValue;
            var bestD = centre;
            for (var d = centre - Fps; d <= centre + Fps; d++)
            {
                var g = SegmentGain(cues, start, end, d, prefix);
                if (g > best)
                {
                    best = g;
                    bestD = d;
                }
            }

            for (var i = start; i < end; i++)
            {
                deltas[i] = bestD;
            }
        }
    }

    private static long[] Onsets(SpeechSignal speech)
    {
        var list = new List<long>();
        for (var t = 1; t < speech.Length; t++)
        {
            if (speech.Frames[t] >= 128 && speech.Frames[t - 1] < 128)
            {
                list.Add(t * (long)SpeechSignal.FrameMs);
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Median of (cue start − nearest speech onset within ±1.5 s) and the share of cues that had such an onset.
    /// </summary>
    private static (long Lead, double Matched) MedianLead(List<SubtitleCue> cues, int start, int end, long[] onsets)
    {
        var leads = new List<long>();
        for (var i = start; i < end; i++)
        {
            var t = cues[i].StartMs;
            var j = Array.BinarySearch(onsets, t);
            if (j < 0)
            {
                j = ~j;
            }

            long? best = null;
            foreach (var k in new[] { j - 1, j })
            {
                if (k >= 0 && k < onsets.Length && Math.Abs(onsets[k] - t) <= 1500 && (best is null || Math.Abs(t - onsets[k]) < Math.Abs(best.Value)))
                {
                    best = t - onsets[k];
                }
            }

            if (best.HasValue)
            {
                leads.Add(best.Value);
            }
        }

        if (leads.Count == 0)
        {
            return (0, 0);
        }

        leads.Sort();
        return (leads[leads.Count / 2], (double)leads.Count / Math.Max(1, end - start));
    }

    private static List<(int Start, int End)> SegmentsOf(int[] deltas)
    {
        var list = new List<(int Start, int End)>();
        var start = 0;
        for (var i = 1; i <= deltas.Length; i++)
        {
            if (i == deltas.Length || deltas[i] != deltas[i - 1])
            {
                list.Add((start, i));
                start = i;
            }
        }

        return list;
    }

    private static double SegmentGain(List<SubtitleCue> cues, int start, int end, int delta, double[] prefix)
    {
        double g = 0;
        for (var i = start; i < end; i++)
        {
            g += RangeSum(prefix, (cues[i].StartMs / SpeechSignal.FrameMs) + delta, (cues[i].EndMs / SpeechSignal.FrameMs) + delta);
        }

        return g;
    }

    /// <summary>
    /// Real cut differences (other intro, ad breaks) produce few long segments; short ones are overfitting noise.
    /// </summary>
    private static void MergeShortSegments(List<SubtitleCue> cues, int[] deltas, double[] prefix)
    {
        var minCues = Math.Min(MinSegmentCues, cues.Count);
        while (true)
        {
            var segs = SegmentsOf(deltas);
            if (segs.Count <= 1)
            {
                return;
            }

            var shortest = segs.Select((s, idx) => (s, idx)).OrderBy(x => x.s.End - x.s.Start).First();
            if (shortest.s.End - shortest.s.Start >= minCues)
            {
                return;
            }

            var (s0, e0) = shortest.s;
            var candidates = new List<int>();
            if (shortest.idx > 0)
            {
                candidates.Add(deltas[segs[shortest.idx - 1].Start]);
            }

            if (shortest.idx < segs.Count - 1)
            {
                candidates.Add(deltas[segs[shortest.idx + 1].Start]);
            }

            var best = candidates.OrderByDescending(d => SegmentGain(cues, s0, e0, d, prefix)).First();
            for (var i = s0; i < e0; i++)
            {
                deltas[i] = best;
            }
        }
    }

    /// <summary>
    /// Weighted mean z-score of each segment's chosen offset against all offsets in the refinement window.
    /// </summary>
    private static double SegmentProminence(List<SubtitleCue> cues, int[] deltas, double[] prefix)
    {
        double weighted = 0;
        var totalCues = 0;
        foreach (var (start, end) in SegmentsOf(deltas))
        {
            var chosen = deltas[start];
            double sum = 0, sumSq = 0, best = SegmentGain(cues, start, end, chosen, prefix);
            var n = 0;
            for (var d = chosen - ProminenceWindowFrames; d <= chosen + ProminenceWindowFrames; d += 5)
            {
                var g = SegmentGain(cues, start, end, d, prefix);
                sum += g;
                sumSq += g * g;
                n++;
            }

            var mean = sum / n;
            var std = Math.Sqrt(Math.Max(1e-9, (sumSq / n) - (mean * mean)));
            weighted += (best - mean) / std * (end - start);
            totalCues += end - start;
        }

        return totalCues == 0 ? 0 : weighted / totalCues;
    }

    private static void Fft(Complex[] a, bool invert)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = 2 * Math.PI / len * (invert ? 1 : -1);
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var j = 0; j < len / 2; j++)
                {
                    var u = a[i + j];
                    var t = a[i + j + (len / 2)] * w;
                    a[i + j] = u + t;
                    a[i + j + (len / 2)] = u - t;
                    w *= wlen;
                }
            }
        }

        if (invert)
        {
            for (var i = 0; i < n; i++)
            {
                a[i] /= n;
            }
        }
    }
}
