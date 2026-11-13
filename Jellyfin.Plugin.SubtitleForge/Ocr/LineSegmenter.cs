using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Splits a subtitle image into single text lines using the horizontal ink projection.
/// </summary>
public static class LineSegmenter
{
    private const byte InkThreshold = 140;

    /// <summary>
    /// Splits an image into tightly cropped, padded line images (top to bottom).
    /// </summary>
    /// <param name="image">Ink-on-white image.</param>
    /// <returns>Line images.</returns>
    public static List<GrayImage> Split(GrayImage image)
    {
        var rowInk = new int[image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var count = 0;
            var offset = y * image.Width;
            for (var x = 0; x < image.Width; x++)
            {
                if (image.Pixels[offset + x] < InkThreshold)
                {
                    count++;
                }
            }

            rowInk[y] = count;
        }

        // Collect bands of inked rows.
        var bands = new List<(int Start, int End)>();
        var start = -1;
        for (var y = 0; y <= image.Height; y++)
        {
            var inked = y < image.Height && rowInk[y] > 0;
            if (inked && start < 0)
            {
                start = y;
            }
            else if (!inked && start >= 0)
            {
                bands.Add((start, y - 1));
                start = -1;
            }
        }

        if (bands.Count == 0)
        {
            return new List<GrayImage>();
        }

        // Merge bands separated by tiny gaps or that are too thin to be a line on their own
        // (umlaut dots, accents, descender fragments) into their neighbours.
        var tallest = 0;
        foreach (var b in bands)
        {
            tallest = Math.Max(tallest, b.End - b.Start + 1);
        }

        // 1) Bands separated by 1–2 empty rows are one line (anti-aliasing gaps, broken glyphs).
        var merged = new List<(int Start, int End)> { bands[0] };
        for (var i = 1; i < bands.Count; i++)
        {
            if (bands[i].Start - merged[^1].End - 1 <= 2)
            {
                merged[^1] = (merged[^1].Start, bands[i].End);
            }
            else
            {
                merged.Add(bands[i]);
            }
        }

        // 2) Thin bands (umlaut dots, accents, stray descenders) join their nearer neighbour; on a tie the one below,
        //    because diacritics sit above their letters.
        tallest = merged.Max(m => m.End - m.Start + 1);
        for (var changed = true; changed && merged.Count > 1;)
        {
            changed = false;
            for (var i = 0; i < merged.Count; i++)
            {
                if (merged[i].End - merged[i].Start + 1 >= tallest / 3)
                {
                    continue;
                }

                var gapUp = i > 0 ? merged[i].Start - merged[i - 1].End : int.MaxValue;
                var gapDown = i < merged.Count - 1 ? merged[i + 1].Start - merged[i].End : int.MaxValue;
                if (gapDown <= gapUp)
                {
                    merged[i + 1] = (merged[i].Start, merged[i + 1].End);
                }
                else
                {
                    merged[i - 1] = (merged[i - 1].Start, merged[i].End);
                }

                merged.RemoveAt(i);
                changed = true;
                break;
            }
        }

        // Tight line spacing: umlaut dots or descenders can bridge the gap between two lines, leaving no empty row.
        // Split bands that contain a clear valley (almost no ink) in their middle part.
        var final = new List<(int Start, int End)>();
        foreach (var band in merged)
        {
            SplitAtValleys(rowInk, band.Start, band.End, final);
        }

        // Splitting can cut off diacritics (Ä/Ö/Ü dots, accents): attach thin pieces to the line below them.
        var tallestFinal = final.Max(b => b.End - b.Start + 1);
        for (var i = final.Count - 1; i >= 0; i--)
        {
            if (final.Count > 1 && final[i].End - final[i].Start + 1 < tallestFinal / 3)
            {
                if (i + 1 < final.Count)
                {
                    final[i + 1] = (final[i].Start, final[i + 1].End);
                }
                else
                {
                    final[i - 1] = (final[i - 1].Start, final[i].End);
                }

                final.RemoveAt(i);
            }
        }

        var lines = new List<GrayImage>();
        foreach (var (s, e) in final)
        {
            int left = image.Width, right = -1;
            for (var y = s; y <= e; y++)
            {
                var offset = y * image.Width;
                for (var x = 0; x < image.Width; x++)
                {
                    if (image.Pixels[offset + x] < InkThreshold)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }
                }
            }

            var h = e - s + 1;
            if (right < left || h < 4)
            {
                continue;
            }

            lines.Add(image.Crop(left, s, right - left + 1, h, pad: Math.Max(4, h / 6)));
        }

        return lines;
    }

    private static void SplitAtValleys(int[] rowInk, int start, int end, List<(int Start, int End)> output)
    {
        var height = end - start + 1;
        if (height >= 16)
        {
            double mean = 0;
            for (var y = start; y <= end; y++)
            {
                mean += rowInk[y];
            }

            mean /= height;
            var from = start + (int)(height * 0.3);
            var to = end - (int)(height * 0.3);
            var bestRow = -1;
            var bestInk = int.MaxValue;
            for (var y = from; y <= to; y++)
            {
                if (rowInk[y] < bestInk)
                {
                    bestInk = rowInk[y];
                    bestRow = y;
                }
            }

            if (bestRow >= 0 && bestInk <= mean * 0.15)
            {
                // Several near-empty rows (gap above the umlaut dots and gap below them): cut at the topmost one,
                // so diacritics stay with the letters below them.
                var tolerance = bestInk + Math.Max(2, mean * 0.03);
                for (var y = from; y <= to; y++)
                {
                    if (rowInk[y] <= tolerance)
                    {
                        bestRow = y;
                        break;
                    }
                }

                SplitAtValleys(rowInk, start, bestRow, output);
                SplitAtValleys(rowInk, bestRow + 1, end, output);
                return;
            }
        }

        output.Add((start, end));
    }
}
