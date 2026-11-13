using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Calibration against real films. Runs only when SF_REAL_DIR points to extracted material:
/// re26_eng.wav / re26_ger.wav (16 kHz mono), re26_eng_uk.srt (official, ground truth), house_eng.wav, house_ger.wav.
/// </summary>
public class RealMediaSyncTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("SF_REAL_DIR");
    private static readonly Dictionary<string, SpeechSignal> Cache = new();
    private readonly ITestOutputHelper _out;

    public RealMediaSyncTests(ITestOutputHelper output) => _out = output;

    private static bool Available => Dir is not null && File.Exists(Path.Combine(Dir, "re26_eng.wav"));

    [Fact]
    public void Calibration_Report()
    {
        if (!Available)
        {
            return;
        }

        var reference = Srt.Parse(File.ReadAllText(Path.Combine(Dir!, "re26_eng_uk.srt"), Encoding.UTF8));
        var eng = Signal("re26_eng.wav");
        var ger = Signal("re26_ger.wav");
        var house = Signal("house_eng.wav");

        if (File.Exists(Path.Combine(Dir!, "re26c_eng.wav")))
        {
            _out.WriteLine("RE26 lead EN-ref vs EN centre onsets: " + AnimeSyncTests.Lead(reference, Signal("re26c_eng.wav")));
        }

        _out.WriteLine("House n/a; RE26 lead vs EN downmix onsets: " + AnimeSyncTests.Lead(reference, eng));
        Report("orig subs vs EN audio", reference, eng, reference);
        Report("orig subs vs DE dub audio", reference, ger, reference);
        Report("+7.3 s vs EN", Shift(reference, 7300), eng, reference);
        Report("-41 s vs DE dub", Shift(reference, -41000), ger, reference);
        Report("PAL 25fps +2 s vs EN", reference.Select(c => new SubtitleCue((long)(c.StartMs / (25 / 23.976)) + 2000, (long)(c.EndMs / (25 / 23.976)) + 2000, c.Text)).ToList(), eng, reference);
        Report("split +12 s after half vs EN", reference.Select((c, i) => i >= reference.Count / 2 ? c with { StartMs = c.StartMs + 12000, EndMs = c.EndMs + 12000 } : c).ToList(), eng, reference);
        Report("cut 3 s at 3 points vs DE", reference.Select((c, i) => { var d = (i / (reference.Count / 4)) * 3000L; return c with { StartMs = c.StartMs + d, EndMs = c.EndMs + d }; }).ToList(), ger, reference);
        if (File.Exists(Path.Combine(Dir!, "re26c_eng.wav")))
        {
            var ceng = Signal("re26c_eng.wav");
            var cger = Signal("re26c_ger.wav");
            Report("CENTER orig vs EN", reference, ceng, reference);
            Report("CENTER orig vs DE dub", reference, cger, reference);
            Report("CENTER +7.3 s vs EN", Shift(reference, 7300), ceng, reference);
            Report("CENTER -41 s vs DE dub", Shift(reference, -41000), cger, reference);
            Report("CENTER PAL +2 s vs EN", reference.Select(c => new SubtitleCue((long)(c.StartMs / (25 / 23.976)) + 2000, (long)(c.EndMs / (25 / 23.976)) + 2000, c.Text)).ToList(), ceng, reference);
            Report("CENTER split +12 s vs EN", reference.Select((c, i) => i >= reference.Count / 2 ? c with { StartMs = c.StartMs + 12000, EndMs = c.EndMs + 12000 } : c).ToList(), ceng, reference);
            Report("CENTER cut 3x3 s vs DE", reference.Select((c, i) => { var d = (i / (reference.Count / 4)) * 3000L; return c with { StartMs = c.StartMs + d, EndMs = c.EndMs + d }; }).ToList(), cger, reference);
            Report("CENTER WRONG reversed vs EN", Reverse(reference), ceng, null);
        }

        Report("WRONG: RE subs vs House audio", reference, house, null);
        Report("WRONG: reversed RE timings vs EN", Reverse(reference), eng, null);
    }

    private static List<SubtitleCue> Shift(List<SubtitleCue> cues, long ms) => cues.Select(c => c with { StartMs = c.StartMs + ms, EndMs = c.EndMs + ms }).ToList();

    private static List<SubtitleCue> Reverse(List<SubtitleCue> cues)
    {
        var end = cues.Max(c => c.EndMs);
        return cues.Select(c => new SubtitleCue(end - c.EndMs, end - c.StartMs, c.Text)).OrderBy(c => c.StartMs).ToList();
    }

    private SpeechSignal Signal(string file)
    {
        if (Cache.TryGetValue(file, out var s))
        {
            return s;
        }

        using var fs = File.OpenRead(Path.Combine(Dir!, file));
        SeekToData(fs);
        var sw = Stopwatch.StartNew();
        s = SpeechAnalyzer.AnalyzePcm(fs, CancellationToken.None);
        _out.WriteLine($"VAD {file}: {sw.Elapsed.TotalSeconds:F1}s for {s.Length / 6000.0:F0} min, speech {s.SpeechRatio():P0}");
        Cache[file] = s;
        return s;
    }

    private void Report(string name, List<SubtitleCue> cues, SpeechSignal speech, List<SubtitleCue>? truth)
    {
        var sw = Stopwatch.StartNew();
        var r = SubtitleAligner.Align(cues, speech);
        var err = string.Empty;
        if (truth is not null)
        {
            var e = truth.Zip(r.Cues, (a, b) => Math.Abs(a.StartMs - b.StartMs)).OrderBy(x => x).ToList();
            err = $" | err median {e[e.Count / 2]} ms, p90 {e[(int)(e.Count * 0.9)]} ms, p99 {e[(int)(e.Count * 0.99)]} ms";
        }

        _out.WriteLine($"{name,-34} confident={r.IsConfident,-5} {r}{err} ({sw.ElapsedMilliseconds} ms)");
    }

    private static void SeekToData(Stream s)
    {
        var br = new BinaryReader(s);
        s.Seek(12, SeekOrigin.Begin);
        while (s.Position < s.Length)
        {
            var id = Encoding.ASCII.GetString(br.ReadBytes(4));
            var size = br.ReadUInt32();
            if (id == "data")
            {
                return;
            }

            s.Seek(size + (size % 2), SeekOrigin.Current);
        }
    }
}
