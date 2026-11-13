using System.Diagnostics;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Uses a synthetic film (tools/make_synth_film.py): TTS speech at known times over music and noise.
/// </summary>
public class SyncTests
{
    private static readonly Lazy<SpeechSignal?> Speech = new(() =>
    {
        var wav = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synth", "film.wav");
        if (!File.Exists(wav))
        {
            return null;
        }

        using var fs = File.OpenRead(wav);
        fs.Seek(44, SeekOrigin.Begin); // canonical PCM WAV header
        return SpeechAnalyzer.AnalyzePcm(fs, CancellationToken.None);
    });

    private readonly ITestOutputHelper _out;

    public SyncTests(ITestOutputHelper output) => _out = output;

    private static List<SubtitleCue> Reference => Srt.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "synth", "reference.srt")));

    [Fact]
    public void Vad_DetectsTheSpokenSentences()
    {
        var speech = Speech.Value;
        Assert.NotNull(speech);
        var sw = Stopwatch.StartNew();
        var wav = Path.Combine(AppContext.BaseDirectory, "Fixtures", "synth", "film.wav");
        using (var fs = File.OpenRead(wav))
        {
            fs.Seek(44, SeekOrigin.Begin);
            SpeechAnalyzer.AnalyzePcm(fs, CancellationToken.None);
        }

        var minutes = speech!.Length / 6000.0;
        _out.WriteLine($"VAD: {sw.ElapsedMilliseconds} ms for {minutes:F1} min audio → {sw.ElapsedMilliseconds / minutes / 1000 * 120:F0} s per 2 h film; speech ratio {speech.SpeechRatio():P0}");

        var result = SubtitleAligner.Align(Reference, speech, allowSplits: false);
        _out.WriteLine("reference: " + result);
        Assert.True(result.SpeechOverlapBefore > 0.75, $"overlap {result.SpeechOverlapBefore:P0}");
    }

    [Theory]
    [InlineData(3500)]
    [InlineData(-12250)]
    [InlineData(700)]
    [InlineData(95000)]
    public void Align_RecoversConstantOffset(long offsetMs)
    {
        var shifted = Reference.Select(c => c with { StartMs = c.StartMs + offsetMs, EndMs = c.EndMs + offsetMs }).ToList();
        var result = SubtitleAligner.Align(shifted, Speech.Value!);
        _out.WriteLine(result.ToString());
        AssertCloseToReference(result);
        Assert.True(result.IsConfident);
        Assert.Equal(1, result.Segments);
    }

    [Theory]
    [InlineData(25 / 23.976, 1000)]
    [InlineData(23.976 / 25, -2000)]
    public void Align_RecoversFramerateAndOffset(double ratio, long offsetMs)
    {
        // Subtitle made for a release with another framerate: times are scaled.
        var distorted = Reference.Select(c => new SubtitleCue((long)(c.StartMs / ratio) + offsetMs, (long)(c.EndMs / ratio) + offsetMs, c.Text)).ToList();
        var result = SubtitleAligner.Align(distorted, Speech.Value!);
        _out.WriteLine(result.ToString());
        AssertCloseToReference(result);
        Assert.True(result.IsConfident);
    }

    [Fact]
    public void Align_RecoversSplitOffset()
    {
        // Release with a different cut: from cue 19 on everything is 4.2 s later; plus a global 1.5 s offset.
        var distorted = Reference.Select((c, i) =>
        {
            var d = 1500 + (i >= 18 ? 4200 : 0);
            return c with { StartMs = c.StartMs + d, EndMs = c.EndMs + d };
        }).ToList();
        var result = SubtitleAligner.Align(distorted, Speech.Value!);
        _out.WriteLine(result.ToString());
        AssertCloseToReference(result);
        Assert.True(result.Segments >= 2);
        Assert.True(result.IsConfident);
    }

    [Fact]
    public void Align_KeepsCorrectSubtitleUntouched()
    {
        var result = SubtitleAligner.Align(Reference, Speech.Value!);
        _out.WriteLine(result.ToString());
        Assert.Equal(1, result.Segments);
        Assert.True(Math.Abs(result.OffsetMs) <= 60);
        Assert.True(result.IsConfident);
    }

    [Fact]
    public void Align_RejectsSubtitleForDifferentContent()
    {
        // Timing that has nothing to do with this audio (another episode).
        var rnd = new Random(3);
        var t = 2000L;
        var wrong = new List<SubtitleCue>();
        for (var i = 0; i < 36; i++)
        {
            t += rnd.Next(400, 9000);
            var d = rnd.Next(1200, 3500);
            wrong.Add(new SubtitleCue(t, t + d, "x"));
            t += d;
        }

        var result = SubtitleAligner.Align(wrong, Speech.Value!);
        _out.WriteLine(result.ToString());
        Assert.False(result.IsConfident);
    }

    private void AssertCloseToReference(AlignmentResult result)
    {
        var errors = Reference.Zip(result.Cues, (r, c) => Math.Abs(r.StartMs - c.StartMs)).OrderBy(e => e).ToList();
        var median = errors[errors.Count / 2];
        var p90 = errors[(int)(errors.Count * 0.9)];
        _out.WriteLine($"start error: median {median} ms, p90 {p90} ms, max {errors[^1]} ms");
        // The synthetic reference starts exactly at speech onset; real subtitles lead by ~180 ms, which the
        // aligner targets after a correction (calibrated on real Blu-ray/WEB subtitles). Allow for that.
        Assert.True(median <= 250, $"median error {median} ms");
        Assert.True(p90 <= 350, $"p90 error {p90} ms");
    }
}
