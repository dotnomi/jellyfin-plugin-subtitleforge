using System.Diagnostics;
using Jellyfin.Plugin.SubtitleForge.Sync;
using Jellyfin.Plugin.SubtitleForge.Whisper;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Whisper speed/quality check. Runs only when SF_WHISPER_DIR contains ggml-*-q5_1.bin and *.pcm (16 kHz s16le) excerpts.
/// </summary>
public class WhisperSpikeTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("SF_WHISPER_DIR");
    private readonly ITestOutputHelper _out;

    public WhisperSpikeTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("base", "house_ger", "de", false)]
    [InlineData("small", "house_ger", "de", false)]
    [InlineData("small", "house_eng", "en", false)]
    [InlineData("small", "er_ger", "de", false)]
    [InlineData("small", "er_jpn", "ja", true)]
    [InlineData("small", "er_ger", "de", true)]
    [InlineData("small", "house_ger", "de", true)]
    public async Task Spike(string model, string clip, string lang, bool translate)
    {
        if (Dir is null)
        {
            return;
        }

        var modelPath = Path.Combine(Dir, $"ggml-{model}-q5_1.bin");
        var pcm = Path.Combine(Dir, clip + ".pcm");
        if (!File.Exists(modelPath) || !File.Exists(pcm))
        {
            return;
        }

        SpeechSignal speech;
        await using (var fs = File.OpenRead(pcm))
        {
            speech = SpeechAnalyzer.AnalyzePcm(fs, CancellationToken.None);
        }

        var windows = WhisperEngine.SpeechWindows(speech);
        var audioMin = new FileInfo(pcm).Length / 32000.0 / 60;
        var sw = Stopwatch.StartNew();
        var segments = await WhisperEngine.TranscribeAsync(modelPath, pcm, windows, lang, translate, 4, null, CancellationToken.None);
        sw.Stop();
        var cues = WhisperPostProcessor.ToCues(segments, speech);
        _out.WriteLine($"runtime: {WhisperEngine.LoadedRuntime} ({WhisperEngine.GpuStatus})");
        _out.WriteLine($"{model}/{clip} ({lang}{(translate ? "→en" : string.Empty)}): {audioMin:F1} min audio, {windows.Count} windows " +
            $"({windows.Sum(w => w.End - w.Start) / 60000.0:F1} min), {sw.Elapsed.TotalSeconds:F0}s → {sw.Elapsed.TotalSeconds / 60 / audioMin:F2}× realtime, " +
            $"{segments.Count} segments → {cues.Count} cues");
        foreach (var c in cues.Take(14))
        {
            _out.WriteLine($"  {c.StartMs / 1000.0,7:F1}s  {c.Text.Replace("\n", " / ")}");
        }

        Assert.NotEmpty(cues);
    }
}
