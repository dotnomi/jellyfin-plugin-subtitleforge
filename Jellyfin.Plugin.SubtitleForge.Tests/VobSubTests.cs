using Jellyfin.Plugin.SubtitleForge.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

public class VobSubTests
{
    // One stop-delay tick is 1024/90000 s (~11.4 ms).
    private const long Tick = 12;

    private readonly ITestOutputHelper _out;

    public VobSubTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Decoder_TimingsForcedAndReassembly(bool mpeg1, bool writeTimestamps)
    {
        var entries = new[]
        {
            new VobSubTestWriter.Entry(1000, 2500, "Hallo"),
            new VobSubTestWriter.Entry(3000, 6000, "Zwei\nZeilen", Forced: true),
            new VobSubTestWriter.Entry(7000, null, "Ohne Stopp"),
            new VobSubTestWriter.Entry(9000, null, "Letzter"),
            new VobSubTestWriter.Entry(3_725_250, 3_727_000, "Nach einer Stunde"),
        };
        var vob = VobSubTestWriter.Write(entries, Font("NotoSans-SemiBold.ttf"), packSize: 512, mpeg1: mpeg1, writeTimestamps: writeTimestamps);
        Assert.All(vob.PesPerSpu, n => Assert.True(n >= 2, "every SPU should span several PES packets"));

        var subs = VobSubParser.Parse(vob.Idx, new MemoryStream(vob.Sub));

        Assert.Equal(5, subs.Count);
        Assert.Equal(1000, subs[0].StartMs);
        Assert.InRange(subs[0].EndMs, 2500 - Tick, 2500 + Tick);
        Assert.False(subs[0].Forced);
        Assert.Equal(3000, subs[1].StartMs);
        Assert.InRange(subs[1].EndMs, 6000 - Tick, 6000 + Tick);
        Assert.True(subs[1].Forced);
        Assert.Equal(7000, subs[2].StartMs);
        Assert.Equal(9000, subs[2].EndMs); // no stop: next start
        Assert.Equal(9000, subs[3].StartMs);
        Assert.Equal(14000, subs[3].EndMs); // no stop: 5 s cap
        Assert.Equal(3_725_250, subs[4].StartMs);
        Assert.InRange(subs[4].EndMs, 3_727_000 - Tick, 3_727_000 + Tick);

        // Placed at the bottom centre of a 720x576 frame; the two-line subtitle splits into two lines.
        var (x, y, image) = subs[1].Images[0];
        Assert.InRange(x, 200, 360);
        Assert.InRange(y + image.Height, 500, 576);
        Assert.Equal(2, LineSegmenter.Split(image).Count);
    }

    [Fact]
    public void Decoder_RendersTextAsInkOutlineAsWhite()
    {
        foreach (var invert in new[] { false, true })
        {
            var vob = VobSubTestWriter.Write(new[] { new VobSubTestWriter.Entry(0, 1000, "Ol") }, Font("DejaVuSans.ttf"), 40, invertColors: invert);
            var image = VobSubParser.Parse(vob.Idx, new MemoryStream(vob.Sub))[0].Images[0].Image;

            // Ink must be the glyph interior: the centre column of the "l" stroke is dark,
            // while the crop border (background / outer outline) stays white.
            var dark = image.Pixels.Count(p => p < 100);
            var white = image.Pixels.Count(p => p == 255);
            Assert.True(dark > 50, $"invert={invert}: dark={dark}");
            Assert.True(white > dark, $"invert={invert}: white={white} dark={dark}");
            for (var x = 0; x < image.Width; x++)
            {
                Assert.Equal(255, image[x, 0]);
                Assert.Equal(255, image[x, image.Height - 1]);
            }
        }
    }

    [Fact]
    public void Decoder_IgnoresOtherSubstreamsAndGarbage()
    {
        var vob = VobSubTestWriter.Write(new[] { new VobSubTestWriter.Entry(500, 1500, "Test") }, Font("DejaVuSans.ttf"));
        var withNoise = new byte[] { 0, 0, 1, 0xBE, 0, 4, 0xFF, 0xFF, 0xFF, 0xFF, 0x12, 0x34 }.Concat(vob.Sub).ToArray();

        // filepos values in the .idx are now off by the prefix, so the PES PTS is used.
        var subs = VobSubParser.Parse(vob.Idx, new MemoryStream(withNoise));
        Assert.Single(subs);
        Assert.Equal(500, subs[0].StartMs);
    }

    [Theory]
    [InlineData("NotoSans-SemiBold.ttf", 30f)]
    [InlineData("DejaVuSans.ttf", 28f)]
    [InlineData("LiberationSans-Regular.ttf", 28f)]
    public void Ocr_CharacterAccuracyAbove97Percent(string font, float size)
    {
        var entries = OcrTests.Sentences.Select((s, i) => new VobSubTestWriter.Entry(i * 4000L, (i * 4000L) + 3000, s)).ToList();
        var vob = VobSubTestWriter.Write(entries, Font(font), size);
        Assert.Contains(vob.PesPerSpu, n => n >= 2);

        var subs = VobSubParser.Parse(vob.Idx, new MemoryStream(vob.Sub));
        Assert.Equal(entries.Count, subs.Count);

        using var recognizer = new PaddleRecognizer();
        var result = ImageSubtitleOcr.Convert(subs, recognizer, null, CancellationToken.None);

        Assert.Equal(OcrTests.Sentences.Length, result.Cues.Count);
        int errors = 0, total = 0;
        for (var i = 0; i < OcrTests.Sentences.Length; i++)
        {
            var expected = OcrTests.Sentences[i];
            var cue = result.Cues[i];
            Assert.Equal(i * 4000L, cue.StartMs);
            Assert.InRange(cue.EndMs, (i * 4000L) + 3000 - Tick, (i * 4000L) + 3000 + Tick);
            var d = Levenshtein(expected, cue.Text);
            errors += d;
            total += expected.Length;
            if (d > 0)
            {
                _out.WriteLine($"[{d}] expected: {expected.Replace("\n", " | ")}\n    actual:   {cue.Text.Replace("\n", " | ")}");
            }
        }

        var accuracy = 1.0 - ((double)errors / total);
        _out.WriteLine($"{font}: accuracy {accuracy:P2}, mean conf {result.MeanConfidence:F3}, low-conf lines {result.LowConfidenceLines}");
        Assert.True(accuracy > 0.97, $"accuracy {accuracy:P2}");
    }

    /// <summary>
    /// Muxes the generated pair into MKV with ffmpeg and extracts it again with <c>-f vobsub</c>,
    /// so the parser is checked against ffmpeg's own .idx/.sub layout. Skipped when ffmpeg is not installed.
    /// </summary>
    [Fact]
    public void JellyfinFfmpeg_VobRemuxWithProbedPalette()
    {
        // remux.vob: the 3-cue test file muxed into MKV and copied out by jellyfin-ffmpeg 8 with "-c copy -f vob"
        // (how the plugin extracts DVD subtitles); remux.probe.txt: ffprobe -show_streams -show_data of that stream.
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vobsub");
        var idx = Jellyfin.Plugin.SubtitleForge.Common.FfmpegRunner.ParseExtradata(File.ReadAllText(Path.Combine(dir, "remux.probe.txt")));
        Assert.NotNull(idx);
        Assert.StartsWith("size: 720x576", idx, StringComparison.Ordinal);
        Assert.Contains("palette:", idx, StringComparison.Ordinal);

        using var vob = File.OpenRead(Path.Combine(dir, "remux.vob"));
        var subs = VobSubParser.Parse(idx!, vob);
        Assert.Equal(3, subs.Count);
        Assert.InRange(subs[0].StartMs, 1000 - Tick, 1000 + Tick);
        Assert.InRange(subs[0].EndMs, 2500 - (2 * Tick), 2500 + (2 * Tick));
        Assert.InRange(subs[1].StartMs, 3000 - Tick, 3000 + Tick);
        Assert.True(subs[1].Forced);
        Assert.InRange(subs[2].StartMs, 65_432 - Tick, 65_432 + Tick);

        using var recognizer = new PaddleRecognizer();
        var result = ImageSubtitleOcr.Convert(subs, recognizer, null, CancellationToken.None);
        Assert.Equal(new[] { "Hallo Welt", "Zwei\nZeilen", "Später" }, result.Cues.Select(c => c.Text));
    }

    [Fact]
    public void Ffmpeg_MkvRoundTrip()
    {
        if (!HasFfmpeg())
        {
            _out.WriteLine("ffmpeg not found, skipping");
            return;
        }

        var entries = new[]
        {
            new VobSubTestWriter.Entry(1000, 2500, "Hallo Welt"),
            new VobSubTestWriter.Entry(3000, 6000, "Zwei\nZeilen", Forced: true),
            new VobSubTestWriter.Entry(65_432, 68_000, "Später"),
        };
        // Not DejaVu at 30 px: LineSegmenter's valley split cuts "Zwei" below the Z top bar there (PGS alike).
        var vob = VobSubTestWriter.Write(entries, Font("LiberationSans-Regular.ttf"));
        var dir = Directory.CreateTempSubdirectory("vobsub").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "in.idx"), vob.Idx);
            File.WriteAllBytes(Path.Combine(dir, "in.sub"), vob.Sub);
            RunFfmpeg(dir, "-i in.idx -map 0 -c:s copy film.mkv");
            if (Run(dir, "ffmpeg", "-hide_banner -muxers").Contains(" vobsub ", StringComparison.Ordinal))
            {
                RunFfmpeg(dir, "-i film.mkv -map 0:s:0 -c:s copy -f vobsub out.idx");
            }
            else if (HasTool("mkvextract"))
            {
                // Older ffmpeg (< 7) has no vobsub muxer; mkvextract writes the same classic layout.
                Run(dir, "mkvextract", "film.mkv tracks 0:out.idx");
            }
            else
            {
                _out.WriteLine("neither ffmpeg vobsub muxer nor mkvextract available, skipping");
                return;
            }

            var dump = Environment.GetEnvironmentVariable("SF_DUMP_VOBSUB");
            if (!string.IsNullOrEmpty(dump))
            {
                foreach (var f in new[] { "in.idx", "in.sub", "out.idx", "out.sub" })
                {
                    File.Copy(Path.Combine(dir, f), Path.Combine(dump, f), true);
                }
            }

            var idx = File.ReadAllText(Path.Combine(dir, "out.idx"));
            _out.WriteLine(idx);
            using var sub = File.OpenRead(Path.Combine(dir, "out.sub"));
            var subs = VobSubParser.Parse(idx, sub);

            Assert.Equal(3, subs.Count);
            Assert.Equal(1000, subs[0].StartMs);
            Assert.InRange(subs[0].EndMs, 2500 - Tick, 2500 + Tick);
            Assert.Equal(3000, subs[1].StartMs);
            Assert.True(subs[1].Forced);
            Assert.Equal(65_432, subs[2].StartMs);

            using var recognizer = new PaddleRecognizer();
            var result = ImageSubtitleOcr.Convert(subs, recognizer, null, CancellationToken.None);
            Assert.Equal(entries.Select(e => e.Text), result.Cues.Select(c => c.Text));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static bool HasFfmpeg() => HasTool("ffmpeg");

    private static bool HasTool(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':').Any(p => File.Exists(Path.Combine(p, name)));

    private static void RunFfmpeg(string dir, string args) => Run(dir, "ffmpeg", "-hide_banner -loglevel error -y " + args);

    private static string Run(string dir, string tool, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(tool, args)
        {
            WorkingDirectory = dir,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} {args} failed: {err}{stdout.Result}");
        return stdout.Result;
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
