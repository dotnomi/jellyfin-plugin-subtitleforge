using SkiaSharp;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Test helper: renders text like a Blu-ray subtitle (white fill, black outline, anti-aliased)
/// and encodes it as a real PGS (.sup) stream.
/// </summary>
public static class PgsTestWriter
{
    public sealed record Entry(long StartMs, long EndMs, string Text);

    public static byte[] Write(IEnumerable<Entry> entries, string fontPath, float fontSize = 52, int videoWidth = 1920, int videoHeight = 1080, float lineSpacing = 1.3f)
    {
        using var typeface = SKTypeface.FromFile(fontPath) ?? throw new FileNotFoundException(fontPath);
        using var ms = new MemoryStream();
        ushort compNum = 0;
        foreach (var e in entries)
        {
            var (indices, w, h) = Render(e.Text, typeface, fontSize, lineSpacing);
            var x = (videoWidth - w) / 2;
            var y = videoHeight - h - 60;

            // Display set: show.
            var pcs = new List<byte>();
            pcs.AddRange(U16(videoWidth)); pcs.AddRange(U16(videoHeight)); pcs.Add(0x10);
            pcs.AddRange(U16(compNum++)); pcs.Add(0x80); pcs.Add(0); pcs.Add(0); pcs.Add(1);
            pcs.AddRange(U16(0)); pcs.Add(0); pcs.Add(0); pcs.AddRange(U16(x)); pcs.AddRange(U16(y));
            Segment(ms, e.StartMs, 0x16, pcs.ToArray());

            var wds = new List<byte> { 1, 0 };
            wds.AddRange(U16(x)); wds.AddRange(U16(y)); wds.AddRange(U16(w)); wds.AddRange(U16(h));
            Segment(ms, e.StartMs, 0x17, wds.ToArray());

            var pds = new List<byte> { 0, 0 };
            for (var i = 0; i < 256; i++)
            {
                var (luma, alpha) = PaletteEntry(i);
                pds.Add((byte)i); pds.Add(luma); pds.Add(128); pds.Add(128); pds.Add(alpha);
            }

            Segment(ms, e.StartMs, 0x14, pds.ToArray());

            var rle = EncodeRle(indices, w, h);
            const int firstMax = 65535 - 11;
            const int nextMax = 65535 - 4;
            var pos = 0;
            var first = true;
            while (first || pos < rle.Length)
            {
                var max = first ? firstMax : nextMax;
                var take = Math.Min(max, rle.Length - pos);
                var last = pos + take >= rle.Length;
                var ods = new List<byte>();
                ods.AddRange(U16(0)); ods.Add(0);
                ods.Add((byte)((first ? 0x80 : 0) | (last ? 0x40 : 0)));
                if (first)
                {
                    var len = rle.Length + 4;
                    ods.Add((byte)(len >> 16)); ods.Add((byte)(len >> 8)); ods.Add((byte)len);
                    ods.AddRange(U16(w)); ods.AddRange(U16(h));
                }

                ods.AddRange(rle.AsSpan(pos, take).ToArray());
                Segment(ms, e.StartMs, 0x15, ods.ToArray());
                pos += take;
                first = false;
            }

            Segment(ms, e.StartMs, 0x80, Array.Empty<byte>());

            // Display set: clear.
            var clear = new List<byte>();
            clear.AddRange(U16(videoWidth)); clear.AddRange(U16(videoHeight)); clear.Add(0x10);
            clear.AddRange(U16(compNum++)); clear.Add(0x00); clear.Add(0); clear.Add(0); clear.Add(0);
            Segment(ms, e.EndMs, 0x16, clear.ToArray());
            Segment(ms, e.EndMs, 0x17, wds.ToArray());
            Segment(ms, e.EndMs, 0x80, Array.Empty<byte>());
        }

        return ms.ToArray();
    }

    // Palette: index 0 transparent; 1..255 = 16 luma levels x 16 alpha levels (alpha level > 0).
    private static (byte Luma, byte Alpha) PaletteEntry(int i)
    {
        if (i == 0)
        {
            return (16, 0);
        }

        var k = i - 1; // 0..254
        var lumaLevel = k % 16;
        var alphaLevel = (k / 16) + 1; // 1..16
        return ((byte)(16 + (lumaLevel * 219 / 15)), (byte)Math.Min(255, alphaLevel * 16 - 1));
    }

    private static int Quantize(byte luma, byte alpha)
    {
        if (alpha < 8)
        {
            return 0;
        }

        var alphaLevel = Math.Clamp((alpha + 8) / 16, 1, 15);
        var lumaLevel = Math.Clamp((int)Math.Round((luma - 16) * 15 / 219.0), 0, 15);
        return 1 + ((alphaLevel - 1) * 16) + lumaLevel;
    }

    private static (byte[] Indices, int W, int H) Render(string text, SKTypeface typeface, float size, float lineSpacing)
    {
        using var font = new SKFont(typeface, size);
        var lines = text.Split('\n');
        var lineHeight = (int)Math.Ceiling(size * lineSpacing);
        var stroke = size * 0.12f;
        var w = (int)Math.Ceiling(lines.Max(l => font.MeasureText(l)) + (stroke * 2) + 8);
        var h = (lineHeight * lines.Length) + 8;

        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var outline = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke * 2, StrokeJoin = SKStrokeJoin.Round };
            using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
            for (var i = 0; i < lines.Length; i++)
            {
                var lw = font.MeasureText(lines[i]);
                var x = (w - lw) / 2;
                var baseline = 4 + (i * lineHeight) + (size * 1.05f);
                canvas.DrawText(lines[i], x, baseline, SKTextAlign.Left, font, outline);
                canvas.DrawText(lines[i], x, baseline, SKTextAlign.Left, font, fill);
            }
        }

        var idx = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                var luma = (byte)(16 + (((0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue)) * 219 / 255));
                idx[(y * w) + x] = (byte)Quantize(luma, c.Alpha);
            }
        }

        return (idx, w, h);
    }

    private static byte[] EncodeRle(byte[] px, int w, int h)
    {
        var o = new List<byte>();
        for (var y = 0; y < h; y++)
        {
            var x = 0;
            while (x < w)
            {
                var c = px[(y * w) + x];
                var run = 1;
                while (x + run < w && px[(y * w) + x + run] == c && run < 16383)
                {
                    run++;
                }

                if (c == 0)
                {
                    if (run < 64)
                    {
                        o.Add(0); o.Add((byte)run);
                    }
                    else
                    {
                        o.Add(0); o.Add((byte)(0x40 | (run >> 8))); o.Add((byte)run);
                    }
                }
                else if (run < 3)
                {
                    for (var i = 0; i < run; i++)
                    {
                        o.Add(c);
                    }
                }
                else if (run < 64)
                {
                    o.Add(0); o.Add((byte)(0x80 | run)); o.Add(c);
                }
                else
                {
                    o.Add(0); o.Add((byte)(0xC0 | (run >> 8))); o.Add((byte)run); o.Add(c);
                }

                x += run;
            }

            o.Add(0); o.Add(0);
        }

        return o.ToArray();
    }

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static void Segment(Stream s, long ms, byte type, byte[] data)
    {
        var pts = (uint)(ms * 90);
        s.Write(new byte[] { (byte)'P', (byte)'G', (byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts, 0, 0, 0, 0, type, (byte)(data.Length >> 8), (byte)data.Length });
        s.Write(data);
    }
}
