using System;
using System.IO;

namespace Jellyfin.Plugin.SubtitleForge.Sync;

/// <summary>
/// Speech probability over time at 100 frames per second (10 ms), quantised to bytes.
/// </summary>
public sealed class SpeechSignal
{
    /// <summary>Frame length in ms.</summary>
    public const int FrameMs = 10;

    private const uint Magic = 0x53464753; // "SGFS"

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechSignal"/> class.
    /// </summary>
    /// <param name="frames">Probabilities 0..255 per 10 ms frame.</param>
    public SpeechSignal(byte[] frames)
    {
        Frames = frames;
    }

    /// <summary>Gets the frames.</summary>
    public byte[] Frames { get; }

    /// <summary>Gets the length in frames.</summary>
    public int Length => Frames.Length;

    /// <summary>
    /// Builds a signal from per-window VAD probabilities (512 samples @ 16 kHz = 32 ms each).
    /// </summary>
    /// <param name="windowProbs">Probabilities.</param>
    /// <returns>Signal.</returns>
    public static SpeechSignal FromWindows(ReadOnlySpan<float> windowProbs)
    {
        const double windowMs = 512 * 1000.0 / 16000;
        var total = (int)Math.Ceiling(windowProbs.Length * windowMs / FrameMs);
        var frames = new byte[total];
        for (var f = 0; f < total; f++)
        {
            var w = Math.Min(windowProbs.Length - 1, (int)(f * FrameMs / windowMs));
            frames[f] = (byte)Math.Clamp((int)Math.Round(windowProbs[w] * 255), 0, 255);
        }

        return new SpeechSignal(frames);
    }

    /// <summary>
    /// Fraction of frames that contain speech.
    /// </summary>
    /// <returns>0..1.</returns>
    public double SpeechRatio()
    {
        var n = 0;
        foreach (var b in Frames)
        {
            if (b >= 128)
            {
                n++;
            }
        }

        return Frames.Length == 0 ? 0 : (double)n / Frames.Length;
    }

    /// <summary>
    /// Writes the signal to a cache file.
    /// </summary>
    /// <param name="path">Path.</param>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path + ".tmp");
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(Frames.Length);
            w.Write(Frames);
        }

        File.Move(path + ".tmp", path, true);
    }

    /// <summary>
    /// Loads a cached signal.
    /// </summary>
    /// <param name="path">Path.</param>
    /// <returns>Signal or null.</returns>
    public static SpeechSignal? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadUInt32() != Magic)
        {
            return null;
        }

        var len = r.ReadInt32();
        var frames = r.ReadBytes(len);
        return frames.Length == len ? new SpeechSignal(frames) : null;
    }
}
