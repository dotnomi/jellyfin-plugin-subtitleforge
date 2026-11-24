using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Parser/decoder for DVD VobSub subtitles (.idx text + .sub MPEG program stream). Pure managed code.
/// </summary>
/// <remarks>
/// The output reuses <see cref="PgsSubtitle"/> so the OCR pipeline can consume both formats.
/// </remarks>
public static class VobSubParser
{
    private const long MissingStopCapMs = 5000;
    private const int CropMargin = 4;

    /// <summary>
    /// Parses a VobSub pair into displayed subtitles.
    /// </summary>
    /// <param name="idxText">Contents of the .idx file.</param>
    /// <param name="sub">The .sub (MPEG-PS) data.</param>
    /// <returns>Subtitles in display order.</returns>
    public static List<PgsSubtitle> Parse(string idxText, Stream sub)
    {
        ArgumentNullException.ThrowIfNull(idxText);
        ArgumentNullException.ThrowIfNull(sub);

        var idx = ParseIdx(idxText);
        var data = Jellyfin.Plugin.SubtitleForge.Common.SafeHttp.ReadLimited(sub, OcrLimits.MaxVobSubBytes)
            ?? throw new InvalidDataException("DVD subtitle stream exceeds the size limit");

        var packets = ReadSpuPackets(data);
        if (packets.Count == 0)
        {
            return new List<PgsSubtitle>();
        }

        // Pick the substream named by the first "id:" section of the .idx, else the first one seen.
        var streamId = 0x20 + idx.StreamIndex;
        if (!packets.Any(p => p.StreamId == streamId))
        {
            streamId = packets[0].StreamId;
        }

        long pixels = 0;
        long decodedPixels = 0;
        var decoded = new List<Decoded>();
        foreach (var p in packets.Where(p => p.StreamId == streamId))
        {
            long baseMs;
            if (idx.Timestamps.TryGetValue(p.FilePos, out var ts))
            {
                baseMs = ts;
            }
            else if (p.PtsMs is long pts)
            {
                baseMs = pts + idx.DelayMs;
            }
            else
            {
                continue;
            }

            var d = DecodeSpu(p.Data, idx.Palette);
            if (d is null)
            {
                continue;
            }

            d.StartMs += baseMs;
            if (d.StopMs is long stop)
            {
                d.StopMs = stop + baseMs;
            }

            // Charge the full decoded area (not just the cropped image): it is what each subtitle costs to decode.
            decodedPixels += d.RawPixels;
            if (decodedPixels > OcrLimits.MaxDecodedPixels)
            {
                throw new InvalidDataException("Image subtitle stream exceeds the size limit");
            }

            if (d.Image is not null)
            {
                pixels += (long)d.Image.Width * d.Image.Height;
                if (pixels > OcrLimits.MaxStreamPixels)
                {
                    throw new InvalidDataException("Image subtitle stream exceeds the size limit");
                }
            }

            decoded.Add(d);
        }

        decoded.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        var result = new List<PgsSubtitle>();
        for (var i = 0; i < decoded.Count; i++)
        {
            var d = decoded[i];
            if (d.Image is null)
            {
                continue;
            }

            long end;
            if (d.StopMs is long stop && stop > d.StartMs)
            {
                end = stop;
            }
            else
            {
                end = d.StartMs + MissingStopCapMs;
                if (i + 1 < decoded.Count)
                {
                    end = Math.Min(end, Math.Max(decoded[i + 1].StartMs, d.StartMs + 1));
                }
            }

            result.Add(new PgsSubtitle(d.StartMs, end, d.Forced, new[] { (d.X, d.Y, d.Image) }));
        }

        return result;
    }

    private static IdxInfo ParseIdx(string text)
    {
        var info = new IdxInfo();
        var palette = new uint[16];
        var streamFound = false;
        var collect = true; // only collect timestamps of the first "id:" section

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "palette":
                    var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < parts.Length && i < 16; i++)
                    {
                        if (uint.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                        {
                            palette[i] = rgb & 0xFFFFFF;
                        }
                    }

                    break;
                case "id":
                    if (streamFound)
                    {
                        collect = false;
                        break;
                    }

                    streamFound = true;
                    var ix = value.IndexOf("index:", StringComparison.OrdinalIgnoreCase);
                    if (ix >= 0 && int.TryParse(value[(ix + 6)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    {
                        info.StreamIndex = Math.Clamp(index, 0, 31);
                    }

                    break;
                case "delay":
                    if (collect && TryParseTime(value, out var delay))
                    {
                        info.DelayMs = delay;
                    }

                    break;
                case "timestamp":
                    if (!collect)
                    {
                        break;
                    }

                    var comma = value.IndexOf(',', StringComparison.Ordinal);
                    var fp = value.IndexOf("filepos:", StringComparison.OrdinalIgnoreCase);
                    if (comma > 0 && fp > 0
                        && TryParseTime(value[..comma], out var ms)
                        && long.TryParse(value[(fp + 8)..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pos))
                    {
                        info.Timestamps[pos] = Math.Max(0, ms + info.DelayMs);
                    }

                    break;
            }
        }

        info.Palette = palette;
        return info;
    }

    // "hh:mm:ss:mmm", optionally prefixed with '-' (delay lines).
    private static bool TryParseTime(string s, out long ms)
    {
        ms = 0;
        s = s.Trim();
        var negative = s.StartsWith('-');
        if (negative || s.StartsWith('+'))
        {
            s = s[1..];
        }

        var p = s.Split(':');
        if (p.Length != 4
            || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec)
            || !int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli))
        {
            return false;
        }

        ms = ((((h * 60L) + m) * 60) + sec) * 1000 + milli;
        if (negative)
        {
            ms = -ms;
        }

        return true;
    }

    /// <summary>
    /// Walks the MPEG-PS packs and reassembles complete SPU packets per subpicture substream.
    /// </summary>
    private static List<SpuPacket> ReadSpuPackets(byte[] d)
    {
        var result = new List<SpuPacket>();
        var assembling = new Dictionary<int, SpuPacket>();
        long packPos = 0;
        var i = 0;

        while (i + 4 <= d.Length)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1)
            {
                i++; // resync on the next start code
                continue;
            }

            var code = d[i + 3];
            if (code == 0xBA)
            {
                packPos = i;
                if (i + 5 > d.Length)
                {
                    break;
                }

                if ((d[i + 4] & 0xC0) == 0x40)
                {
                    // MPEG-2 pack header: 14 bytes + stuffing.
                    if (i + 14 > d.Length)
                    {
                        break;
                    }

                    i += 14 + (d[i + 13] & 0x07);
                }
                else
                {
                    i += 12; // MPEG-1 pack header
                }

                continue;
            }

            if (code == 0xB9)
            {
                break; // program end
            }

            if (i + 6 > d.Length)
            {
                break;
            }

            var len = (d[i + 4] << 8) | d[i + 5];
            var payloadStart = i + 6;
            var next = Math.Min(d.Length, payloadStart + len);
            if (code == 0xBD)
            {
                HandlePrivateStream(d, payloadStart, next, packPos, assembling, result);
            }

            i = next;
        }

        return result;
    }

    private static void HandlePrivateStream(byte[] d, int pos, int end, long packPos, Dictionary<int, SpuPacket> assembling, List<SpuPacket> result)
    {
        long? ptsMs = null;
        if (pos < end && (d[pos] & 0xC0) == 0x80)
        {
            // MPEG-2 PES header.
            if (pos + 3 > end)
            {
                return;
            }

            var hasPts = (d[pos + 1] & 0x80) != 0;
            var headerLen = d[pos + 2];
            if (hasPts && pos + 8 <= end)
            {
                ptsMs = ReadPts(d, pos + 3) / 90;
            }

            pos += 3 + headerLen;
        }
        else
        {
            // MPEG-1 PES header: stuffing, optional STD buffer, optional PTS/DTS.
            while (pos < end && d[pos] == 0xFF)
            {
                pos++;
            }

            if (pos < end && (d[pos] & 0xC0) == 0x40)
            {
                pos += 2;
            }

            if (pos < end && (d[pos] & 0xF0) == 0x20)
            {
                if (pos + 5 <= end)
                {
                    ptsMs = ReadPts(d, pos) / 90;
                }

                pos += 5;
            }
            else if (pos < end && (d[pos] & 0xF0) == 0x30)
            {
                if (pos + 5 <= end)
                {
                    ptsMs = ReadPts(d, pos) / 90;
                }

                pos += 10;
            }
            else if (pos < end && d[pos] == 0x0F)
            {
                pos++;
            }
        }

        if (pos >= end)
        {
            return;
        }

        int streamId = d[pos++];
        if (streamId < 0x20 || streamId > 0x3F)
        {
            return; // AC-3, DTS, LPCM etc.
        }

        assembling.TryGetValue(streamId, out var current);
        if (current is null || ptsMs is not null)
        {
            if (end - pos < 2)
            {
                return;
            }

            current = new SpuPacket
            {
                StreamId = streamId,
                FilePos = packPos,
                PtsMs = ptsMs,
                Size = (d[pos] << 8) | d[pos + 1],
            };
            assembling[streamId] = current;
        }

        current.Buffer.Write(d, pos, end - pos);
        if (current.Buffer.Length >= current.Size)
        {
            current.Data = current.Buffer.ToArray();
            if (current.Size >= 4)
            {
                result.Add(current);
            }

            assembling.Remove(streamId);
        }
    }

    private static long ReadPts(byte[] d, int o)
        => ((long)(d[o] & 0x0E) << 29) | ((long)d[o + 1] << 22) | ((long)(d[o + 2] & 0xFE) << 14) | ((long)d[o + 3] << 7) | ((long)d[o + 4] >> 1);

    /// <summary>
    /// Decodes one SPU: control sequences plus the interlaced 2-bit RLE bitmap.
    /// </summary>
    private static Decoded? DecodeSpu(byte[] spu, uint[] palette)
    {
        var size = Math.Min(spu.Length, (spu[0] << 8) | spu[1]);
        var ctrl = (spu[2] << 8) | spu[3];
        if (ctrl < 4 || ctrl + 4 > size)
        {
            return null;
        }

        var result = new Decoded();
        var colors = new[] { 0, 1, 2, 3 };
        var alphas = new[] { 0, 15, 15, 15 };
        int x1 = 0, x2 = -1, y1 = 0, y2 = -1, topOff = -1, bottomOff = -1;
        var started = false;
        var visited = new HashSet<int>();

        var seq = ctrl;
        while (seq + 4 <= size && visited.Add(seq))
        {
            var delayMs = ((spu[seq] << 8) | spu[seq + 1]) * 1024L / 90;
            var nextSeq = (spu[seq + 2] << 8) | spu[seq + 3];
            var p = seq + 4;
            var endOfSeq = false;
            while (p < size && !endOfSeq)
            {
                var cmd = spu[p++];
                switch (cmd)
                {
                    case 0x00: // forced start
                    case 0x01: // start
                        if (!started)
                        {
                            result.StartMs = delayMs;
                            started = true;
                        }

                        result.Forced |= cmd == 0x00;
                        break;
                    case 0x02: // stop
                        if (result.StopMs is null && (!started || delayMs >= result.StartMs))
                        {
                            result.StopMs = delayMs;
                        }

                        break;
                    case 0x03 when p + 2 <= size:
                        colors[3] = spu[p] >> 4;
                        colors[2] = spu[p] & 0x0F;
                        colors[1] = spu[p + 1] >> 4;
                        colors[0] = spu[p + 1] & 0x0F;
                        p += 2;
                        break;
                    case 0x04 when p + 2 <= size:
                        alphas[3] = spu[p] >> 4;
                        alphas[2] = spu[p] & 0x0F;
                        alphas[1] = spu[p + 1] >> 4;
                        alphas[0] = spu[p + 1] & 0x0F;
                        p += 2;
                        break;
                    case 0x05 when p + 6 <= size:
                        x1 = (spu[p] << 4) | (spu[p + 1] >> 4);
                        x2 = ((spu[p + 1] & 0x0F) << 8) | spu[p + 2];
                        y1 = (spu[p + 3] << 4) | (spu[p + 4] >> 4);
                        y2 = ((spu[p + 4] & 0x0F) << 8) | spu[p + 5];
                        p += 6;
                        break;
                    case 0x06 when p + 4 <= size:
                        topOff = (spu[p] << 8) | spu[p + 1];
                        bottomOff = (spu[p + 2] << 8) | spu[p + 3];
                        p += 4;
                        break;
                    case 0x07 when p + 2 <= size:
                        // Colour/contrast change (rare): skip by its declared size.
                        p += (spu[p] << 8) | spu[p + 1];
                        break;
                    default:
                        endOfSeq = true; // 0xFF or unknown command
                        break;
                }
            }

            if (nextSeq == seq)
            {
                break;
            }

            seq = nextSeq;
        }

        var w = x2 - x1 + 1;
        var h = y2 - y1 + 1;
        if (!started || w <= 0 || h <= 0 || topOff < 4 || bottomOff < 4 || w > OcrLimits.MaxDvdWidth || h > OcrLimits.MaxDvdHeight)
        {
            return result.StopMs is null && !started ? null : result;
        }

        result.RawPixels = (long)w * h;
        var pixels = new byte[w * h];
        DecodeField(spu, topOff, ctrl, pixels, w, h, 0);
        DecodeField(spu, bottomOff, ctrl, pixels, w, h, 1);

        var crop = Render(pixels, w, h, colors, alphas, palette, out var cx, out var cy);
        if (crop is not null)
        {
            result.Image = crop;
            result.X = x1 + cx;
            result.Y = y1 + cy;
        }

        return result;
    }

    private static void DecodeField(byte[] spu, int offset, int limit, byte[] pixels, int w, int h, int firstRow)
    {
        var nibble = offset * 2;
        var end = limit * 2;

        int Next()
        {
            if (nibble >= end)
            {
                return 0;
            }

            var b = spu[nibble >> 1];
            var v = (nibble & 1) == 0 ? b >> 4 : b & 0x0F;
            nibble++;
            return v;
        }

        for (var y = firstRow; y < h && nibble < end; y += 2)
        {
            var x = 0;
            while (x < w && nibble < end)
            {
                var v = Next();
                if (v < 0x4)
                {
                    v = (v << 4) | Next();
                    if (v < 0x10)
                    {
                        v = (v << 4) | Next();
                        if (v < 0x40)
                        {
                            v = (v << 4) | Next();
                        }
                    }
                }

                var color = (byte)(v & 0x3);
                var run = v >> 2;
                if (run == 0 || x + run > w)
                {
                    run = w - x; // run to end of line
                }

                Array.Fill(pixels, color, (y * w) + x, run);
                x += run;
            }

            // Each line starts byte-aligned.
            nibble = (nibble + 1) & ~1;
        }
    }

    /// <summary>
    /// Maps the four SPU colour slots to ink-on-white and crops to the content.
    /// The background is the most common slot; the outline is the slot that touches the background most;
    /// text is the opaque slot with the largest luma distance to the outline. Ink is interpolated between
    /// outline (white) and text (black) so anti-aliasing slots become gray.
    /// </summary>
    private static GrayImage? Render(byte[] px, int w, int h, int[] colors, int[] alphas, uint[] palette, out int cropX, out int cropY)
    {
        cropX = cropY = 0;
        var luma = new double[4];
        var visible = new bool[4];
        var counts = new int[4];
        for (var s = 0; s < 4; s++)
        {
            var rgb = palette[colors[s] & 0x0F];
            luma[s] = (0.299 * ((rgb >> 16) & 0xFF)) + (0.587 * ((rgb >> 8) & 0xFF)) + (0.114 * (rgb & 0xFF));
            visible[s] = alphas[s] > 0;
        }

        foreach (var p in px)
        {
            counts[p]++;
        }

        // Background: transparent slot 0 normally; otherwise the most frequent slot.
        var bg = visible[0] ? Array.IndexOf(counts, counts.Max()) : 0;

        var fg = Enumerable.Range(0, 4).Where(s => s != bg && visible[s] && counts[s] > 0).ToList();
        if (fg.Count == 0)
        {
            return null;
        }

        // Outline: the visible slot most often 4-adjacent to the background.
        var touch = new int[4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = px[(y * w) + x];
                if (c == bg)
                {
                    continue;
                }

                if ((x > 0 && px[(y * w) + x - 1] == bg) || (x + 1 < w && px[(y * w) + x + 1] == bg)
                    || (y > 0 && px[((y - 1) * w) + x] == bg) || (y + 1 < h && px[((y + 1) * w) + x] == bg))
                {
                    touch[c]++;
                }
            }
        }

        var ink = new byte[] { 255, 255, 255, 255 };
        if (fg.Count == 1)
        {
            ink[fg[0]] = 0;
        }
        else
        {
            var outline = fg.OrderByDescending(s => touch[s]).First();
            var text = fg.Where(s => s != outline).OrderByDescending(s => Math.Abs(luma[s] - luma[outline])).First();
            var span = luma[text] - luma[outline];
            foreach (var s in fg)
            {
                if (Math.Abs(span) < 1)
                {
                    ink[s] = s == text ? (byte)0 : (byte)255;
                    continue;
                }

                // 0 at the outline luma (white), 1 at the text luma (black); alpha weakens the ink.
                var t = Math.Clamp((luma[s] - luma[outline]) / span, 0, 1) * (alphas[s] / 15.0);
                ink[s] = (byte)Math.Round(255 * (1 - t));
            }
        }

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (ink[px[(y * w) + x]] < 255)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        minX = Math.Max(0, minX - CropMargin);
        minY = Math.Max(0, minY - CropMargin);
        maxX = Math.Min(w - 1, maxX + CropMargin);
        maxY = Math.Min(h - 1, maxY + CropMargin);
        var img = new GrayImage(maxX - minX + 1, maxY - minY + 1);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                img[x - minX, y - minY] = ink[px[(y * w) + x]];
            }
        }

        cropX = minX;
        cropY = minY;
        return img;
    }

    private sealed class IdxInfo
    {
        public uint[] Palette { get; set; } = new uint[16];

        public int StreamIndex { get; set; }

        public long DelayMs { get; set; }

        public Dictionary<long, long> Timestamps { get; } = new();
    }

    private sealed class SpuPacket
    {
        public int StreamId { get; init; }

        public long FilePos { get; init; }

        public long? PtsMs { get; init; }

        public int Size { get; init; }

        public MemoryStream Buffer { get; } = new();

        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    private sealed class Decoded
    {
        public long StartMs { get; set; }

        public long? StopMs { get; set; }

        public long RawPixels { get; set; }

        public bool Forced { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public GrayImage? Image { get; set; }
    }
}
