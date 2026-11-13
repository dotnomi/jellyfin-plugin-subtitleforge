using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Ocr;
using Jellyfin.Plugin.SubtitleForge.Providers;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Anime calibration. Runs only when SF_ANIME_DIR contains, per episode prefix (sg, er):
/// {p}_jpn.wav, {p}_ger.wav (16 kHz mono), {p}_de.sup (Blu-ray PGS = timing reference), {p}_eng.srt (external, unknown sync).
/// </summary>
public class AnimeSyncTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("SF_ANIME_DIR");
    private readonly ITestOutputHelper _out;

    public AnimeSyncTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Anime_Calibration_Report()
    {
        if (Dir is null || !File.Exists(Path.Combine(Dir, "sg_jpn.wav")))
        {
            return;
        }

        var refs = new Dictionary<string, List<SubtitleCue>>();
        var jpn = new Dictionary<string, SpeechSignal>();
        foreach (var p in new[] { "sg", "er" })
        {
            refs[p] = Reference(p);
            jpn[p] = Signal(p + "_jpn.wav");
            var ger = Signal(p + "_ger.wav");
            var reference = refs[p];
            var eng = SubtitleContent.Parse(File.ReadAllBytes(Path.Combine(Dir, p + "_eng.srt")), p + "_eng.srt", null, null)!;

            _out.WriteLine($"--- {p}: {reference.Count} DE reference cues, {eng.Count} EN cues");
            _out.WriteLine($"{p} lead DE-ref vs JP onsets: {Lead(reference, jpn[p])} | vs DE onsets: {Lead(reference, ger)}");
            Report($"{p} DE-ref vs JP audio", reference, jpn[p], reference);
            Report($"{p} DE-ref vs DE dub audio", reference, ger, reference);
            Report($"{p} DE-ref +4.2 s vs JP", Shift(reference, 4200), jpn[p], reference);
            Report($"{p} DE-ref PAL -1.5 s vs JP", reference.Select(c => new SubtitleCue((long)(c.StartMs / (25 / 23.976)) - 1500, (long)(c.EndMs / (25 / 23.976)) - 1500, c.Text)).ToList(), jpn[p], reference);
            Report($"{p} DE-ref OP cut (+8 s after 1/3) vs JP", reference.Select((c, i) => i >= reference.Count / 3 ? c with { StartMs = c.StartMs + 8000, EndMs = c.EndMs + 8000 } : c).ToList(), jpn[p], reference);

            _out.WriteLine($"{p} EN as delivered vs DE-ref: {Agreement(eng, reference)}");
            var engAligned = SubtitleAligner.Align(eng, jpn[p]);
            _out.WriteLine($"{p} EN aligned to JP audio: confident={engAligned.IsConfident} {engAligned}");
            _out.WriteLine($"{p} EN after alignment vs DE-ref: {Agreement(engAligned.Cues, reference)}");
        }

        Report("WRONG: sg DE-ref vs er JP audio", refs["sg"], jpn["er"], null);
        Report("WRONG: er DE-ref vs sg JP audio", refs["er"], jpn["sg"], null);
    }

    /// <summary>Distribution of (cue start − nearest speech onset within ±1.5 s).</summary>
    internal static string Lead(List<SubtitleCue> cues, SpeechSignal speech)
    {
        var onsets = new List<long>();
        for (var t = 1; t < speech.Length; t++)
        {
            if (speech.Frames[t] >= 128 && speech.Frames[t - 1] < 128)
            {
                onsets.Add(t * 10L);
            }
        }

        var arr = onsets.ToArray();
        var leads = new List<long>();
        foreach (var c in cues)
        {
            var i = Array.BinarySearch(arr, c.StartMs);
            if (i < 0)
            {
                i = ~i;
            }

            long? best = null;
            foreach (var j in new[] { i - 1, i })
            {
                if (j >= 0 && j < arr.Length && Math.Abs(arr[j] - c.StartMs) <= 1500 && (best is null || Math.Abs(arr[j] - c.StartMs) < Math.Abs(best.Value)))
                {
                    best = c.StartMs - arr[j];
                }
            }

            if (best.HasValue)
            {
                leads.Add(best.Value);
            }
        }

        leads.Sort();
        return leads.Count == 0 ? "n/a" : $"median {leads[leads.Count / 2]} ms (p25 {leads[leads.Count / 4]}, p75 {leads[leads.Count * 3 / 4]}, n={leads.Count}/{cues.Count})";
    }

    private static List<SubtitleCue> Shift(List<SubtitleCue> cues, long ms) => cues.Select(c => c with { StartMs = c.StartMs + ms, EndMs = c.EndMs + ms }).ToList();

    /// <summary>Best extra shift (±5 s) and hit rate of cue starts within ±150 ms of a reference start.</summary>
    private static string Agreement(List<SubtitleCue> cues, List<SubtitleCue> reference)
    {
        var starts = reference.Select(c => c.StartMs).OrderBy(x => x).ToArray();
        double Hits(long shift)
        {
            var n = 0;
            foreach (var c in cues)
            {
                var t = c.StartMs + shift;
                var i = Array.BinarySearch(starts, t);
                if (i < 0)
                {
                    i = ~i;
                }

                if ((i < starts.Length && Math.Abs(starts[i] - t) <= 150) || (i > 0 && Math.Abs(starts[i - 1] - t) <= 150))
                {
                    n++;
                }
            }

            return (double)n / cues.Count;
        }

        var best = Enumerable.Range(-100, 201).Select(k => k * 50L).Select(s => (s, h: Hits(s))).MaxBy(x => x.h);
        return FormattableString.Invariant($"hits at 0: {Hits(0):P0}, best extra shift {best.s} ms → {best.h:P0}");
    }

    private List<SubtitleCue> Reference(string p)
    {
        var cache = Path.Combine(Dir!, p + "_de_ref.srt");
        if (File.Exists(cache))
        {
            return Srt.Parse(File.ReadAllText(cache));
        }

        using var fs = File.OpenRead(Path.Combine(Dir!, p + "_de.sup"));
        using var rec = new PaddleRecognizer();
        var sw = Stopwatch.StartNew();
        var result = ImageSubtitleOcr.Convert(fs, rec, null, CancellationToken.None);
        _out.WriteLine($"OCR {p}: {result.Cues.Count} cues, conf {result.MeanConfidence:P1}, {sw.Elapsed.TotalSeconds:F1}s");
        File.WriteAllText(cache, Srt.Write(result.Cues), Encoding.UTF8);
        return Srt.Parse(File.ReadAllText(cache));
    }

    private SpeechSignal Signal(string file)
    {
        using var fs = File.OpenRead(Path.Combine(Dir!, file));
        var br = new BinaryReader(fs);
        fs.Seek(12, SeekOrigin.Begin);
        while (fs.Position < fs.Length)
        {
            var id = Encoding.ASCII.GetString(br.ReadBytes(4));
            var size = br.ReadUInt32();
            if (id == "data")
            {
                break;
            }

            fs.Seek(size + (size % 2), SeekOrigin.Current);
        }

        var s = SpeechAnalyzer.AnalyzePcm(fs, CancellationToken.None);
        _out.WriteLine($"VAD {file}: speech {s.SpeechRatio():P0}");
        return s;
    }

    private void Report(string name, List<SubtitleCue> cues, SpeechSignal speech, List<SubtitleCue>? truth)
    {
        var r = SubtitleAligner.Align(cues, speech);
        var err = string.Empty;
        if (truth is not null)
        {
            var e = truth.Zip(r.Cues, (a, b) => Math.Abs(a.StartMs - b.StartMs)).OrderBy(x => x).ToList();
            err = $" | err median {e[e.Count / 2]} ms, p90 {e[(int)(e.Count * 0.9)]} ms";

            // Core guarantee: a result marked confident must be correct.
            if (r.IsConfident)
            {
                Assert.True(e[(int)(e.Count * 0.9)] <= 300, $"{name}: confident but p90 error {e[(int)(e.Count * 0.9)]} ms");
            }
        }

        if (truth is null)
        {
            Assert.False(r.IsConfident, $"{name}: wrong subtitle accepted");
        }

        _out.WriteLine($"{name,-40} confident={r.IsConfident,-5} {r}{err}");
    }
}
