using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using global::Whisper.net;
using global::Whisper.net.LibraryLoader;

namespace Jellyfin.Plugin.SubtitleForge.Whisper;

/// <summary>
/// A raw transcription segment.
/// </summary>
/// <param name="StartMs">Start.</param>
/// <param name="EndMs">End.</param>
/// <param name="Text">Text.</param>
public sealed record WhisperSegment(long StartMs, long EndMs, string Text);

/// <summary>
/// The actual whisper.cpp calls. Shared between the plugin (in-process fallback) and the separate helper process
/// (SubtitleForge.WhisperHost), so it must only depend on Whisper.net.
/// </summary>
public static class WhisperCore
{
    /// <summary>Max length of one processing window (whisper works in 30 s windows).</summary>
    public const int MaxChunkMs = 28_000;

    /// <summary>
    /// Gets the runtime whisper.cpp actually runs on ("Vulkan" = GPU, "Cpu"), once loaded.
    /// </summary>
    public static string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>
    /// Points Whisper.net at the native libraries (expects "&lt;dir of libraryPath&gt;/runtimes/&lt;rid&gt;/libwhisper.so")
    /// and chooses the runtime order. Must be called before the first model is loaded.
    /// </summary>
    /// <param name="libraryPath">A file inside the folder that contains "runtimes".</param>
    /// <param name="useGpu">Try Vulkan first.</param>
    public static void ConfigureNative(string libraryPath, bool useGpu)
    {
        RuntimeOptions.LibraryPath = libraryPath;

        // whisper.cpp needs libgomp.so.1 (OpenMP). Use the system's copy, else the one shipped with the plugin.
        if (!NativeLibrary.TryLoad("libgomp.so.1", out _))
        {
            var dir = Path.GetDirectoryName(libraryPath) ?? AppContext.BaseDirectory;
            var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            var bundled = Path.Combine(dir, "runtimes", rid, "libgomp.so.1");
            if (File.Exists(bundled))
            {
                NativeLibrary.Load(bundled);
            }
        }

        RuntimeOptions.RuntimeLibraryOrder = useGpu
            ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
            : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
    }

    /// <summary>
    /// Transcribes (or translates to English) the given windows of a 16 kHz mono PCM file (s16le).
    /// </summary>
    /// <param name="modelPath">ggml model.</param>
    /// <param name="pcmPath">Raw PCM file.</param>
    /// <param name="windows">Windows to process.</param>
    /// <param name="language">ISO 639-1 language of the audio.</param>
    /// <param name="translateToEnglish">Translate instead of transcribe.</param>
    /// <param name="threads">CPU threads.</param>
    /// <param name="onSegment">Called for every segment (absolute timestamps).</param>
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public static async Task TranscribeAsync(
        string modelPath,
        string pcmPath,
        IReadOnlyList<(long Start, long End)> windows,
        string language,
        bool translateToEnglish,
        int threads,
        Action<WhisperSegment> onSegment,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var factory = WhisperFactory.FromPath(modelPath);
        var builder = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Max(1, threads))
            .WithNoContext()                 // no carry-over between windows: prevents repetition loops
            .WithTemperature(0f)
            .WithNoSpeechThreshold(0.6f);
        if (translateToEnglish)
        {
            builder = builder.WithTranslate();
        }

        await using var processor = builder.Build();
        await using var pcm = File.OpenRead(pcmPath);
        var buffer = new byte[MaxChunkMs * 32];
        for (var i = 0; i < windows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (start, end) = windows[i];
            var offset = start * 32; // 16 kHz * 2 bytes per ms
            var length = (int)Math.Min(buffer.Length, (end - start) * 32);
            if (offset >= pcm.Length)
            {
                break;
            }

            pcm.Seek(offset, SeekOrigin.Begin);
            var read = await pcm.ReadAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            var samples = new float[read / 2];
            for (var k = 0; k < samples.Length; k++)
            {
                samples[k] = (short)(buffer[2 * k] | (buffer[(2 * k) + 1] << 8)) / 32768f;
            }

            await foreach (var seg in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                var text = seg.Text.Trim();
                if (text.Length > 0)
                {
                    onSegment(new WhisperSegment(start + (long)seg.Start.TotalMilliseconds, start + (long)seg.End.TotalMilliseconds, text));
                }
            }

            progress?.Report((i + 1.0) / windows.Count);
        }
    }
}
