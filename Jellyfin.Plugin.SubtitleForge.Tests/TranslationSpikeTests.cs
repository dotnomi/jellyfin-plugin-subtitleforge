using System.Diagnostics;
using Jellyfin.Plugin.SubtitleForge.Translation;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Offline translation check. Runs only when SF_MT_DIR contains model folders ja-en, de-en, en-de.
/// </summary>
public class TranslationSpikeTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("SF_MT_DIR");
    private readonly ITestOutputHelper _out;

    public TranslationSpikeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Tokenizer_RoundTrips()
    {
        if (Dir is null)
        {
            return;
        }

        var spm = SentencePiece.Load(Path.Combine(Dir, "de-en", "source.spm"));
        var pieces = spm.Encode("Ich habe Angst, meinem eigenen Herzen auf den Grund zu gehen.");
        _out.WriteLine(string.Join(" ", pieces));
        Assert.Equal("Ich habe Angst, meinem eigenen Herzen auf den Grund zu gehen.", SentencePiece.Decode(pieces));
    }

    [Theory]
    [InlineData("de-en", new[] { "Ich habe Angst.", "Angst, meinem eigenen Herzen auf den Grund zu gehen.", "Die Zeichnungen sind sehr sauber, finde ich. Das gefällt mir.", "Solange Sie nur an der Oberfläche kratzen, lässt sich Ihr Wesen in den Werken nicht wiedererkennen.", "Sie sind 29, nicht wahr?", "Morgen werden Sie in eine Beobachtungszelle verlegt." })]
    [InlineData("en-de", new[] { "I'm scared.", "Scared of digging down deep into my mind.", "If you don't dig deeper, the readers won't be able to see you in the work.", "You're twenty-nine years old, right?", "Tomorrow you'll be moved to a holding cell." })]
    [InlineData("ja-en", new[] { "怖いんだ。", "自分の心の中に踏み込むのが。", "絵はすごく綺麗だと思います。", "君は29歳だよね？", "明日はどこに行くの？", "ここで何をしているんだ？" })]
    public void Translate(string pair, string[] sentences)
    {
        if (Dir is null || !Directory.Exists(Path.Combine(Dir, pair)))
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        using var mt = new MarianTranslator(Path.Combine(Dir, pair), 4);
        _out.WriteLine($"{pair}: load {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        foreach (var s in sentences)
        {
            var t = mt.Translate(s);
            _out.WriteLine($"  {s}\n    → {t}");
            Assert.False(string.IsNullOrWhiteSpace(t));
        }

        _out.WriteLine($"  {sw.ElapsedMilliseconds / sentences.Length} ms per sentence");
    }
}

/// <summary>Whisper (ja transcription) → offline MT → en / de. Needs SF_WHISPER_DIR and SF_MT_DIR.</summary>
public class PipelineSpikeTests
{
    private readonly ITestOutputHelper _out;

    public PipelineSpikeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task JapaneseAudio_ToEnglishAndGerman()
    {
        var w = Environment.GetEnvironmentVariable("SF_WHISPER_DIR");
        var m = Environment.GetEnvironmentVariable("SF_MT_DIR");
        if (w is null || m is null)
        {
            return;
        }

        var pcm = Path.Combine(w, "er_jpn.pcm");
        Jellyfin.Plugin.SubtitleForge.Sync.SpeechSignal speech;
        await using (var fs = File.OpenRead(pcm))
        {
            speech = Jellyfin.Plugin.SubtitleForge.Sync.SpeechAnalyzer.AnalyzePcm(fs, CancellationToken.None);
        }

        var windows = Jellyfin.Plugin.SubtitleForge.Whisper.WhisperEngine.SpeechWindows(speech);
        var sw = Stopwatch.StartNew();
        var segments = await Jellyfin.Plugin.SubtitleForge.Whisper.WhisperEngine.TranscribeAsync(Path.Combine(w, "ggml-small-q5_1.bin"), pcm, windows, "ja", false, 4, null, CancellationToken.None);
        _out.WriteLine($"whisper ja: {sw.Elapsed.TotalSeconds:F0}s, {segments.Count} segments");
        using var jaEn = new MarianTranslator(Path.Combine(m, "ja-en"), 4);
        using var fugu = new MarianTranslator(Path.Combine(m, "fugu-ja-en"), 4);
        using var enDe = new MarianTranslator(Path.Combine(m, "en-de"), 4);
        sw.Restart();
        foreach (var s in segments.Take(16))
        {
            var en = jaEn.Translate(s.Text);
            var fen = fugu.Translate(s.Text);
            var de = enDe.Translate(fen);
            _out.WriteLine($"{s.StartMs / 1000.0,6:F1}s {s.Text}\n        Opus EN: {en}\n        Fugu EN: {fen}\n        Fugu→DE: {de}");
        }

        using var enJa = new MarianTranslator(Path.Combine(m, "fugu-en-ja"), 4);
        foreach (var e in new[] { "I'm scared of digging down deep into my mind.", "You're twenty-nine years old, right?", "Tomorrow you'll be moved to a holding cell.", "If only I'd done this back then." })
        {
            _out.WriteLine($"  EN→JA: {e} → {enJa.Translate(e)}");
        }

        _out.WriteLine($"MT: {sw.ElapsedMilliseconds / 16} ms per segment (ja→en→de)");
    }
}
