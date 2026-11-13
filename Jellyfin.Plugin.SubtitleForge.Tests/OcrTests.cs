using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

public class OcrTests
{
    public static readonly string[] Sentences =
    {
        "Das ist ein Test-Untertitel\nmit zwei Zeilen und Umlauten.",
        "Wir müssen sofort zurück zur Brücke!",
        "Hör auf! Du weißt genau, was ich meine.",
        "Äpfel, Öl und Übermut – alles größer.",
        "- Wo bist du gewesen?\n- Das geht dich nichts an.",
        "Ich hab's dir doch gesagt, oder?",
        "Er kam um 19:45 Uhr, nicht um 20 Uhr.",
        "I'm not going back there. Never again.",
        "What did you just say to me?",
        "\"Trust me,\" she said, \"it'll be fine.\"",
        "I don't know... maybe tomorrow?",
        "The quick brown fox jumps over the lazy dog.",
        "We've got 3 minutes left. Move!",
        "Liebe Grüße aus München, Köln und Düsseldorf.",
        "Straße, Fußball, Maß und Grieß.",
        "Kannst du mir bitte helfen?\nIch schaffe das nicht allein.",
        "You can't be serious. That's impossible!",
        "Hey! Wait for me!",
        "Das Ergebnis ist eindeutig: 42.",
        "She'll be here by Friday, I promise.",
    };

    private readonly ITestOutputHelper _out;

    public OcrTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void PgsRoundTrip_PreservesTimingAndImages()
    {
        var entries = new[]
        {
            new PgsTestWriter.Entry(1000, 2500, "Hallo"),
            new PgsTestWriter.Entry(3000, 6000, "Zwei\nZeilen"),
        };
        var sup = PgsTestWriter.Write(entries, Font("NotoSans-SemiBold.ttf"));
        var subs = PgsParser.Parse(new MemoryStream(sup));

        Assert.Equal(2, subs.Count);
        Assert.Equal(1000, subs[0].StartMs);
        Assert.Equal(2500, subs[0].EndMs);
        Assert.Equal(3000, subs[1].StartMs);
        Assert.Equal(6000, subs[1].EndMs);
        Assert.Equal(2, LineSegmenter.Split(subs[1].Images[0].Image).Count);
    }

    [Theory]
    [InlineData("NotoSans-SemiBold.ttf", 52f)]
    [InlineData("DejaVuSans.ttf", 46f)]
    [InlineData("LiberationSans-Regular.ttf", 40f)]
    public void Ocr_CharacterAccuracyAbove97Percent(string font, float size)
    {
        var entries = Sentences.Select((s, i) => new PgsTestWriter.Entry(i * 4000L, (i * 4000L) + 3000, s)).ToList();
        var sup = PgsTestWriter.Write(entries, Font(font), size);

        var dump = Environment.GetEnvironmentVariable("SF_DUMP_SUP");
        if (!string.IsNullOrEmpty(dump))
        {
            File.WriteAllBytes(Path.Combine(dump, Path.GetFileNameWithoutExtension(font) + ".sup"), sup);
        }

        using var recognizer = new PaddleRecognizer();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = ImageSubtitleOcr.Convert(new MemoryStream(sup), recognizer, null, CancellationToken.None);
        sw.Stop();

        Assert.Equal(Sentences.Length, result.Cues.Count);
        int errors = 0, total = 0;
        for (var i = 0; i < Sentences.Length; i++)
        {
            var expected = Sentences[i];
            var actual = result.Cues[i].Text;
            var d = Levenshtein(expected, actual);
            errors += d;
            total += expected.Length;
            if (d > 0)
            {
                _out.WriteLine($"[{d}] expected: {expected.Replace("\n", " | ")}\n    actual:   {actual.Replace("\n", " | ")}");
            }
        }

        var accuracy = 1.0 - ((double)errors / total);
        _out.WriteLine($"{font}: accuracy {accuracy:P2}, mean conf {result.MeanConfidence:F3}, low-conf lines {result.LowConfidenceLines}, {sw.ElapsedMilliseconds} ms for {Sentences.Length} subs");
        Assert.True(accuracy > 0.97, $"accuracy {accuracy:P2}");
    }

    [Fact]
    public void Ocr_TightLineSpacingWithUmlautsIsSplit()
    {
        var text = "ALLES, WAS DU WISSEN MUSST,\nIST AUF DIESEM GERÄT.";
        var sup = PgsTestWriter.Write(new[] { new PgsTestWriter.Entry(0, 2000, text) }, Font("DejaVuSans.ttf"), 56, lineSpacing: 1.15f);
        using var recognizer = new PaddleRecognizer();
        var result = ImageSubtitleOcr.Convert(new MemoryStream(sup), recognizer, null, CancellationToken.None);
        _out.WriteLine(result.Cues[0].Text);
        Assert.Equal(text, result.Cues[0].Text);
    }

    [Fact]
    public void Srt_NormalizeStripsStylingKeepsItalics()
    {
        var text = "{\\an8}<font color=\"#ff0000\">Rot</font>  und <I>kursiv</i>\\Nzweite   Zeile";
        Assert.Equal("Rot und <i>kursiv</i>\nzweite Zeile", Srt.NormalizeText(text));
    }

    [Fact]
    public void Srt_WriteAndParseRoundTrip()
    {
        var cues = new List<SubtitleCue> { new(1500, 3250, "Hallo\nWelt"), new(3661001, 3662000, "Später") };
        var parsed = Srt.Parse(Srt.Write(cues));
        Assert.Equal(cues, parsed);
    }

    private static string Font(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}

public class OcrPostProcessorTests
{
    [Theory]
    [InlineData("it'Il be fine", "it'll be fine")]
    [InlineData("She'Il come", "She'll come")]
    [InlineData("wiIl you", "will you")]
    [InlineData("l'm here", "I'm here")]
    [InlineData("| know", "I know")]
    [InlineData("Wait , what ?", "Wait, what?")]
    [InlineData("Alles Ist gut", "Alles Ist gut")]
    [InlineData("IKEA und ICE", "IKEA und ICE")]
    public void Fix(string input, string expected) => Assert.Equal(expected, OcrPostProcessor.Fix(input));
}
