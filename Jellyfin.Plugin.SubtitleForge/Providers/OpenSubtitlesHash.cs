using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// OpenSubtitles "moviehash": file size + 64-bit little-endian sum of the first and last 64 KiB.
/// Identifies the exact video file, so a hash match guarantees correct timing.
/// </summary>
public static class OpenSubtitlesHash
{
    private const int ChunkSize = 65536;

    /// <summary>
    /// Computes the hash.
    /// </summary>
    /// <param name="path">File path.</param>
    /// <returns>16 hex chars, or null if the file is smaller than 128 KiB.</returns>
    public static string? Compute(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        return Compute(fs);
    }

    /// <summary>
    /// Computes the hash of a seekable stream.
    /// </summary>
    /// <param name="stream">Stream.</param>
    /// <returns>Hash.</returns>
    public static string? Compute(Stream stream)
    {
        var length = stream.Length;
        if (length < ChunkSize * 2)
        {
            return null;
        }

        ulong hash = (ulong)length;
        var buffer = new byte[ChunkSize];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        hash = unchecked(hash + Sum(buffer));
        stream.Seek(-ChunkSize, SeekOrigin.End);
        stream.ReadExactly(buffer);
        hash = unchecked(hash + Sum(buffer));
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static ulong Sum(ReadOnlySpan<byte> data)
    {
        ulong sum = 0;
        for (var i = 0; i < data.Length; i += 8)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8)));
        }

        return sum;
    }
}
