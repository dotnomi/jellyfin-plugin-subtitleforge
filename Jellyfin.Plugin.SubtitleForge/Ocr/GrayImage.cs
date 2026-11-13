using System;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Minimal 8-bit grayscale image: 255 = background (white), 0 = ink (black).
/// </summary>
public sealed class GrayImage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GrayImage"/> class filled with white.
    /// </summary>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    public GrayImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height];
        Array.Fill(Pixels, (byte)255);
    }

    /// <summary>Gets the width.</summary>
    public int Width { get; }

    /// <summary>Gets the height.</summary>
    public int Height { get; }

    /// <summary>Gets the row-major pixel buffer.</summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// Gets or sets a pixel.
    /// </summary>
    /// <param name="x">X.</param>
    /// <param name="y">Y.</param>
    public byte this[int x, int y]
    {
        get => Pixels[(y * Width) + x];
        set => Pixels[(y * Width) + x] = value;
    }

    /// <summary>
    /// Copies a rectangle into a new image.
    /// </summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="w">Width.</param>
    /// <param name="h">Height.</param>
    /// <param name="pad">White padding added on every side.</param>
    /// <returns>The crop.</returns>
    public GrayImage Crop(int x, int y, int w, int h, int pad = 0)
    {
        var result = new GrayImage(w + (2 * pad), h + (2 * pad));
        for (var row = 0; row < h; row++)
        {
            Buffer.BlockCopy(Pixels, ((y + row) * Width) + x, result.Pixels, ((row + pad) * result.Width) + pad, w);
        }

        return result;
    }
}
