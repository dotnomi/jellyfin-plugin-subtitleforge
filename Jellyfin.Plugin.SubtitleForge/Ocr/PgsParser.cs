using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// One displayed PGS subtitle: timing plus the rendered images of its composition objects.
/// </summary>
/// <param name="StartMs">Start time in ms.</param>
/// <param name="EndMs">End time in ms.</param>
/// <param name="Forced">Whether any object carries the forced flag.</param>
/// <param name="Images">Rendered objects as ink-on-white images with their screen position (top-to-bottom).</param>
public sealed record PgsSubtitle(long StartMs, long EndMs, bool Forced, IReadOnlyList<(int X, int Y, GrayImage Image)> Images);

/// <summary>
/// Parser/decoder for Blu-ray PGS (.sup) subtitle streams. Pure managed code, no imaging library needed.
/// </summary>
public static class PgsParser
{
    private const byte SegPalette = 0x14;
    private const byte SegObject = 0x15;
    private const byte SegComposition = 0x16;
    private const byte SegWindow = 0x17;
    private const byte SegEnd = 0x80;

    /// <summary>
    /// Parses a .sup stream into displayed subtitles.
    /// </summary>
    /// <param name="stream">The .sup data.</param>
    /// <returns>Subtitles in display order.</returns>
    public static List<PgsSubtitle> Parse(Stream stream)
    {
        var result = new List<PgsSubtitle>();
        var palettes = new Dictionary<int, uint[]>();
        var objects = new Dictionary<int, ObjectData>();
        var header = new byte[13];

        Composition? pending = null; // composition currently on screen, waiting for its end time
        Composition? current = null;  // composition being assembled in the current display set

        while (ReadExactly(stream, header))
        {
            if (header[0] != 'P' || header[1] != 'G')
            {
                throw new InvalidDataException("Not a PGS stream (missing PG magic).");
            }

            var ptsMs = ReadUInt32(header, 2) / 90;
            var type = header[10];
            var size = (header[11] << 8) | header[12];
            var data = new byte[size];
            if (!ReadExactly(stream, data))
            {
                break;
            }

            switch (type)
            {
                case SegComposition:
                    current = ParseComposition(data, ptsMs);
                    if (current.State != 0)
                    {
                        // Epoch start / acquisition point: object and palette caches are reset.
                        if (current.State == 0x80)
                        {
                            objects.Clear();
                            palettes.Clear();
                        }
                    }

                    break;
                case SegPalette:
                    ParsePalette(data, palettes);
                    break;
                case SegObject:
                    ParseObject(data, objects);
                    break;
                case SegWindow:
                    break;
                case SegEnd:
                    if (current is null)
                    {
                        break;
                    }

                    // Any new display set ends the one on screen.
                    if (pending is not null && pending.Rendered.Count > 0)
                    {
                        var end = Math.Max(current.PtsMs, pending.PtsMs + 1);
                        result.Add(new PgsSubtitle(pending.PtsMs, end, pending.Forced, pending.Rendered));
                    }

                    pending = null;
                    if (current.Objects.Count > 0)
                    {
                        palettes.TryGetValue(current.PaletteId, out var palette);
                        foreach (var co in current.Objects)
                        {
                            if (palette is not null && objects.TryGetValue(co.ObjectId, out var obj) && obj.IsComplete)
                            {
                                var img = Render(obj, palette);
                                if (img is not null)
                                {
                                    current.Rendered.Add((co.X, co.Y, img));
                                }
                            }
                        }

                        current.Rendered.Sort((a, b) => a.Y.CompareTo(b.Y));
                        pending = current;
                    }

                    current = null;
                    break;
            }
        }

        if (pending is not null && pending.Rendered.Count > 0)
        {
            result.Add(new PgsSubtitle(pending.PtsMs, pending.PtsMs + 3000, pending.Forced, pending.Rendered));
        }

        return result;
    }

    private static Composition ParseComposition(byte[] d, long ptsMs)
    {
        // width(2) height(2) framerate(1) compNumber(2) state(1) paletteUpdate(1) paletteId(1) count(1)
        var c = new Composition { PtsMs = ptsMs, State = d[7], PaletteId = d[9] };
        int count = d[10];
        var pos = 11;
        for (var i = 0; i < count && pos + 8 <= d.Length; i++)
        {
            var objectId = (d[pos] << 8) | d[pos + 1];
            var flags = d[pos + 3];
            var x = (d[pos + 4] << 8) | d[pos + 5];
            var y = (d[pos + 6] << 8) | d[pos + 7];
            pos += 8;
            if ((flags & 0x80) != 0)
            {
                pos += 8; // cropping rectangle
            }

            if ((flags & 0x40) != 0)
            {
                c.Forced = true;
            }

            c.Objects.Add(new CompositionObject(objectId, x, y));
        }

        return c;
    }

    private static void ParsePalette(byte[] d, Dictionary<int, uint[]> palettes)
    {
        int id = d[0];
        if (!palettes.TryGetValue(id, out var pal))
        {
            pal = new uint[256];
            palettes[id] = pal;
        }

        for (var pos = 2; pos + 5 <= d.Length; pos += 5)
        {
            // entry: index, Y, Cr, Cb, Alpha. Only luma + alpha matter for OCR.
            pal[d[pos]] = ((uint)d[pos + 4] << 8) | d[pos + 1];
        }
    }

    private static void ParseObject(byte[] d, Dictionary<int, ObjectData> objects)
    {
        var id = (d[0] << 8) | d[1];
        var seq = d[3];
        if ((seq & 0x80) != 0)
        {
            var obj = new ObjectData
            {
                Width = (d[7] << 8) | d[8],
                Height = (d[9] << 8) | d[10],
            };
            obj.Rle.Write(d, 11, d.Length - 11);
            obj.IsComplete = (seq & 0x40) != 0;
            objects[id] = obj;
        }
        else if (objects.TryGetValue(id, out var obj))
        {
            obj.Rle.Write(d, 4, d.Length - 4);
            obj.IsComplete = (seq & 0x40) != 0;
        }
    }

    /// <summary>
    /// Decodes the RLE bitmap and flattens it to dark ink on white: the subtitle is composited on black
    /// (so bright fill stays bright and the dark outline melts into the background), then inverted.
    /// </summary>
    private static GrayImage? Render(ObjectData obj, uint[] palette)
    {
        if (obj.Width <= 0 || obj.Height <= 0)
        {
            return null;
        }

        var img = new GrayImage(obj.Width, obj.Height);
        var rle = obj.Rle.GetBuffer();
        var len = (int)obj.Rle.Length;
        int x = 0, y = 0, i = 0;

        void Put(int color, int count)
        {
            var entry = palette[color];
            var luma = (int)(entry & 0xFF);
            var alpha = (int)(entry >> 8);
            var ink = (byte)(255 - (luma * alpha / 255));
            for (var n = 0; n < count && x < obj.Width; n++, x++)
            {
                if (y < obj.Height)
                {
                    img[x, y] = ink;
                }
            }
        }

        while (i < len && y < obj.Height)
        {
            var b = rle[i++];
            if (b != 0)
            {
                Put(b, 1);
                continue;
            }

            if (i >= len)
            {
                break;
            }

            var f = rle[i++];
            if (f == 0)
            {
                x = 0;
                y++;
                continue;
            }

            var run = f & 0x3F;
            if ((f & 0x40) != 0 && i < len)
            {
                run = (run << 8) | rle[i++];
            }

            var color = 0;
            if ((f & 0x80) != 0 && i < len)
            {
                color = rle[i++];
            }

            Put(color, run);
        }

        return img;
    }

    private static uint ReadUInt32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static bool ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private sealed record CompositionObject(int ObjectId, int X, int Y);

    private sealed class Composition
    {
        public long PtsMs { get; init; }

        public int State { get; init; }

        public int PaletteId { get; init; }

        public bool Forced { get; set; }

        public List<CompositionObject> Objects { get; } = new();

        public List<(int X, int Y, GrayImage Image)> Rendered { get; } = new();
    }

    private sealed class ObjectData
    {
        public int Width { get; init; }

        public int Height { get; init; }

        public MemoryStream Rle { get; } = new();

        public bool IsComplete { get; set; }
    }
}
