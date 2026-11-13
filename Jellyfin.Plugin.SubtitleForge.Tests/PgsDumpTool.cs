using Jellyfin.Plugin.SubtitleForge.Ocr;
using Xunit;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>Debug helper: SF_DUMP_PGS=/path/file.sup writes each subtitle object (and its split lines) as PGM next to it.</summary>
public class PgsDumpTool
{
    [Fact]
    public void Dump()
    {
        var sup = Environment.GetEnvironmentVariable("SF_DUMP_PGS");
        if (string.IsNullOrEmpty(sup) || !File.Exists(sup))
        {
            return;
        }

        using (var ocrStream = File.OpenRead(sup))
        using (var rec = new PaddleRecognizer())
        {
            var res = ImageSubtitleOcr.Convert(ocrStream, rec, null, CancellationToken.None);
            File.WriteAllLines(sup + ".ocr.txt", res.Cues.Select(c => c.Text.Replace("\n", " | ")));
        }

        var outDir = Path.Combine(Path.GetDirectoryName(sup)!, Path.GetFileNameWithoutExtension(sup) + "_dump");
        Directory.CreateDirectory(outDir);
        using var fs = File.OpenRead(sup);
        var subs = PgsParser.Parse(fs);
        for (var i = 0; i < subs.Count; i++)
        {
            for (var j = 0; j < subs[i].Images.Count; j++)
            {
                var (x, y, img) = subs[i].Images[j];
                if (i == 3)
                {
                    var prof = new System.Text.StringBuilder();
                    for (var r = 0; r < img.Height; r++)
                    {
                        var c = 0;
                        for (var q = 0; q < img.Width; q++)
                        {
                            if (img[q, r] < 140)
                            {
                                c++;
                            }
                        }

                        prof.Append(r).Append(':').Append(c).Append(' ');
                    }

                    File.WriteAllText(Path.Combine(outDir, "profile_003.txt"), prof.ToString());
                }

                Write(Path.Combine(outDir, $"{i:000}_{j}_obj_{x}x{y}.pgm"), img);
                var lines = LineSegmenter.Split(img);
                for (var k = 0; k < lines.Count; k++)
                {
                    Write(Path.Combine(outDir, $"{i:000}_{j}_line{k}.pgm"), lines[k]);
                }
            }
        }
    }

    private static void Write(string path, GrayImage img)
    {
        using var f = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{img.Width} {img.Height}\n255\n");
        f.Write(header);
        f.Write(img.Pixels);
    }
}
