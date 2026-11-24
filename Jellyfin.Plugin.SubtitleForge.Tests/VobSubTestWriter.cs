using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Jellyfin.Plugin.SubtitleForge.Tests;

/// <summary>
/// Test helper: renders text like a DVD subtitle (white fill, black outline, gray anti-alias slot,
/// transparent background) and encodes it as a real VobSub pair (.idx text + MPEG-PS .sub).
/// </summary>
public static class VobSubTestWriter
{
    // Palette slots used in the SPU colour command (deliberately not 0..3 to exercise the lookup).
    private const int PalBackground = 0;
    private const int PalFill = 15;
    private const int PalOutline = 1;
    private const int PalAntiAlias = 7;

    /// <summary>One subtitle. EndMs null = no stop command.</summary>
    public sealed record Entry(long StartMs, long? EndMs, string Text, bool Forced = false);

    /// <summary>Generated files plus the number of PES packets each SPU was split into.</summary>
    public sealed record Result(string Idx, byte[] Sub, List<int> PesPerSpu);

    public static Result Write(
        IEnumerable<Entry> entries,
        string fontPath,
        float fontSize = 30,
        int videoWidth = 720,
        int videoHeight = 576,
        int packSize = 2048,
        bool mpeg1 = false,
        bool writeTimestamps = true,
        bool invertColors = false)
    {
        using var typeface = SKTypeface.FromFile(fontPath) ?? throw new FileNotFoundException(fontPath);
        using var sub = new MemoryStream();
        var idx = new StringBuilder();
        idx.Append("# VobSub index file, v7 (do not modify this line!)\n");
        idx.Append(CultureInfo.InvariantCulture, $"size: {videoWidth}x{videoHeight}\n");
        var palette = new string[16];
        for (var i = 0; i < 16; i++)
        {
            palette[i] = "808080";
        }

        palette[PalBackground] = "000000";
        palette[PalFill] = invertColors ? "101010" : "f0f0f0";
        palette[PalOutline] = invertColors ? "f0f0f0" : "101010";
        palette[PalAntiAlias] = "808080";
        idx.Append("palette: ").Append(string.Join(", ", palette)).Append('\n');
        idx.Append("custom colors: OFF, tridx: 0000, colors: 000000, 000000, 000000, 000000\n");
        idx.Append("langidx: 0\n");
        idx.Append("id: de, index: 0\n");

        var pesCounts = new List<int>();
        foreach (var e in entries)
        {
            var (px, w, h) = Render(e.Text, typeface, fontSize);
            var x = (videoWidth - w) / 2;
            var y = videoHeight - h - 30;
            var spu = BuildSpu(px, w, h, x, y, e);
            var filePos = sub.Position;
            if (writeTimestamps)
            {
                var t = TimeSpan.FromMilliseconds(e.StartMs);
                idx.Append(CultureInfo.InvariantCulture, $"timestamp: {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}:{t.Milliseconds:000}, filepos: {filePos:x9}\n");
            }

            pesCounts.Add(WritePacks(sub, spu, e.StartMs * 90, packSize, mpeg1));
        }

        // Program end code.
        sub.Write(new byte[] { 0, 0, 1, 0xB9 });
        return new Result(idx.ToString(), sub.ToArray(), pesCounts);
    }

    private static byte[] BuildSpu(byte[] px, int w, int h, int x, int y, Entry e)
    {
        var top = EncodeField(px, w, h, 0);
        var bottom = EncodeField(px, w, h, 1);
        var topOff = 4;
        var bottomOff = topOff + top.Length;
        var ctrl = bottomOff + bottom.Length;

        var seq1 = new List<byte>();
        var hasStop = e.EndMs is not null;
        var seq1Len = 4 + 1 + 3 + 3 + 7 + 5 + 1;
        var seq2Off = ctrl + seq1Len;
        seq1.AddRange(U16(0));
        seq1.AddRange(U16(hasStop ? seq2Off : ctrl));
        seq1.Add(e.Forced ? (byte)0x00 : (byte)0x01);
        seq1.Add(0x03); seq1.Add((PalAntiAlias << 4) | PalOutline); seq1.Add((PalFill << 4) | PalBackground);
        seq1.Add(0x04); seq1.Add(0xFF); seq1.Add(0xF0);
        var x2 = x + w - 1;
        var y2 = y + h - 1;
        seq1.Add(0x05);
        seq1.Add((byte)(x >> 4)); seq1.Add((byte)(((x & 0x0F) << 4) | (x2 >> 8))); seq1.Add((byte)x2);
        seq1.Add((byte)(y >> 4)); seq1.Add((byte)(((y & 0x0F) << 4) | (y2 >> 8))); seq1.Add((byte)y2);
        seq1.Add(0x06); seq1.AddRange(U16(topOff)); seq1.AddRange(U16(bottomOff));
        seq1.Add(0xFF);
        if (seq1.Count != seq1Len)
        {
            throw new InvalidOperationException("seq1 length");
        }

        var seq2 = new List<byte>();
        if (hasStop)
        {
            var delay = (int)Math.Round((e.EndMs!.Value - e.StartMs) * 90 / 1024.0);
            seq2.AddRange(U16(delay));
            seq2.AddRange(U16(seq2Off));
            seq2.Add(0x02);
            seq2.Add(0xFF);
        }

        var total = ctrl + seq1.Count + seq2.Count;
        var spu = new List<byte>(total);
        spu.AddRange(U16(total));
        spu.AddRange(U16(ctrl));
        spu.AddRange(top);
        spu.AddRange(bottom);
        spu.AddRange(seq1);
        spu.AddRange(seq2);
        return spu.ToArray();
    }

    private static byte[] EncodeField(byte[] px, int w, int h, int firstRow)
    {
        var nibbles = new List<int>();
        void Code(int run, int color)
        {
            var v = (run << 2) | color;
            if (run == 0)
            {
                nibbles.AddRange(new[] { 0, 0, 0, color });
            }
            else if (run < 4)
            {
                nibbles.Add(v);
            }
            else if (run < 16)
            {
                nibbles.Add(v >> 4); nibbles.Add(v & 0xF);
            }
            else if (run < 64)
            {
                nibbles.Add(v >> 8); nibbles.Add((v >> 4) & 0xF); nibbles.Add(v & 0xF);
            }
            else
            {
                nibbles.Add(v >> 12); nibbles.Add((v >> 8) & 0xF); nibbles.Add((v >> 4) & 0xF); nibbles.Add(v & 0xF);
            }
        }

        for (var y = firstRow; y < h; y += 2)
        {
            var x = 0;
            while (x < w)
            {
                var c = px[(y * w) + x];
                var run = 1;
                while (x + run < w && px[(y * w) + x + run] == c)
                {
                    run++;
                }

                if (x + run == w && run > 3)
                {
                    Code(0, c); // fill to end of line
                }
                else
                {
                    var left = run;
                    while (left > 0)
                    {
                        var take = Math.Min(left, 255);
                        Code(take, c);
                        left -= take;
                    }
                }

                x += run;
            }

            if ((nibbles.Count & 1) != 0)
            {
                nibbles.Add(0);
            }
        }

        var bytes = new byte[nibbles.Count / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((nibbles[2 * i] << 4) | nibbles[(2 * i) + 1]);
        }

        return bytes;
    }

    private static int WritePacks(Stream s, byte[] spu, long pts, int packSize, bool mpeg1)
    {
        var pos = 0;
        var count = 0;
        while (pos < spu.Length)
        {
            var first = pos == 0;
            var pack = new List<byte>();
            pack.AddRange(mpeg1 ? Mpeg1PackHeader(pts) : Mpeg2PackHeader(pts));

            var pesHeader = new List<byte>();
            if (mpeg1)
            {
                if (first)
                {
                    pesHeader.AddRange(Pts(pts, 0x20));
                }
                else
                {
                    pesHeader.Add(0x0F);
                }
            }
            else
            {
                pesHeader.Add(0x81);
                pesHeader.Add(first ? (byte)0x80 : (byte)0x00);
                pesHeader.Add(first ? (byte)5 : (byte)0);
                if (first)
                {
                    pesHeader.AddRange(Pts(pts, 0x20));
                }
            }

            var room = packSize - pack.Count - 6 - pesHeader.Count - 1;
            var take = Math.Min(room, spu.Length - pos);
            var leftover = room - take;
            if (leftover is > 0 and < 6)
            {
                take -= 6 - leftover; // make room for a padding packet
                leftover = 6;
            }

            pack.AddRange(new byte[] { 0, 0, 1, 0xBD });
            pack.AddRange(U16(pesHeader.Count + 1 + take));
            pack.AddRange(pesHeader);
            pack.Add(0x20);
            pack.AddRange(spu.AsSpan(pos, take).ToArray());
            if (leftover >= 6)
            {
                pack.AddRange(new byte[] { 0, 0, 1, 0xBE });
                pack.AddRange(U16(leftover - 6));
                pack.AddRange(Enumerable.Repeat((byte)0xFF, leftover - 6));
            }

            s.Write(pack.ToArray());
            pos += take;
            count++;
        }

        return count;
    }

    private static byte[] Mpeg2PackHeader(long scr) => new byte[]
    {
        0, 0, 1, 0xBA,
        (byte)(0x44 | ((scr >> 27) & 0x38) | ((scr >> 28) & 0x03)),
        (byte)(scr >> 20),
        (byte)(((scr >> 12) & 0xF8) | 0x04 | ((scr >> 13) & 0x03)),
        (byte)(scr >> 5),
        (byte)(((scr << 3) & 0xF8) | 0x04),
        0x01,
        0x01, 0x89, 0xC3,
        0xF8, // no stuffing
    };

    private static byte[] Mpeg1PackHeader(long scr)
    {
        var p = Pts(scr, 0x20);
        return new byte[] { 0, 0, 1, 0xBA, p[0], p[1], p[2], p[3], p[4], 0x80, 0x1B, 0x83 };
    }

    private static byte[] Pts(long pts, int prefix) => new[]
    {
        (byte)(prefix | (int)((pts >> 29) & 0x0E) | 1),
        (byte)(pts >> 22),
        (byte)(((pts >> 14) & 0xFE) | 1),
        (byte)(pts >> 7),
        (byte)(((pts << 1) & 0xFE) | 1),
    };

    // Slots: 0 transparent background, 1 fill, 2 outline, 3 anti-alias (gray).
    private static (byte[] Pixels, int W, int H) Render(string text, SKTypeface typeface, float size)
    {
        using var font = new SKFont(typeface, size);
        var lines = text.Split('\n');
        var lineHeight = (int)Math.Ceiling(size * 1.3f);
        var stroke = size * 0.1f;
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

        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                var luma = (0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue);
                px[(y * w) + x] = c.Alpha < 128 ? (byte)0 : luma > 170 ? (byte)1 : luma < 85 ? (byte)2 : (byte)3;
            }
        }

        return (px, w, h);
    }

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
}
