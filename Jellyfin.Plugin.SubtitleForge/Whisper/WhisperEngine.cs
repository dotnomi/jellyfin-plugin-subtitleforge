using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Sync;
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
/// Runs whisper.cpp (CPU) over the speech regions of an audio track.
/// </summary>
public static class WhisperEngine
{
    /// <summary>Max length of one processing window (whisper works in 30 s windows).</summary>
    public const int MaxChunkMs = 28_000;

    private static int _configured;

    /// <summary>
    /// Gets the runtime whisper.cpp actually runs on ("Vulkan" = GPU, "Cpu"), once loaded.
    /// </summary>
    public static string LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString() ?? "noch nicht geladen";

    /// <summary>Gets the result of the GPU check (why Vulkan is or is not used).</summary>
    public static string GpuStatus { get; private set; } = "noch nicht geprüft";

    /// <summary>
    /// Points Whisper.net at the native libraries inside the plugin folder (plugins are not covered by the host's probing).
    /// </summary>
    public static void EnsureNativeLibrary()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        // Whisper.net looks for "<dir of LibraryPath>/runtimes/<rid>/libwhisper.so".
        var assembly = typeof(WhisperEngine).Assembly.Location;
        RuntimeOptions.LibraryPath = assembly;

        // whisper.cpp needs libgomp.so.1 (OpenMP). Use the system's copy, else the one shipped with the plugin.
        if (!NativeLibrary.TryLoad("libgomp.so.1", out _))
        {
            var dir = Path.GetDirectoryName(assembly) ?? AppContext.BaseDirectory;
            var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            var bundled = Path.Combine(dir, "runtimes", rid, "libgomp.so.1");
            if (File.Exists(bundled))
            {
                NativeLibrary.Load(bundled);
            }
        }

        // Vulkan first (AMD, Intel, NVIDIA GPUs); whisper.net falls back to the CPU runtime automatically when no
        // Vulkan driver/GPU is present. Decided once per process (native libraries cannot be unloaded).
        var useGpu = Environment.GetEnvironmentVariable("SF_WHISPER_GPU") is { } env
            ? env == "1"
            : Plugin.Instance?.Configuration.WhisperUseGpu ?? true;
        var gpu = GpuDetector.Check();
        GpuStatus = useGpu ? gpu.Description : "GPU-Beschleunigung ausgeschaltet";
        RuntimeOptions.RuntimeLibraryOrder = useGpu && gpu.Usable
            ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
            : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
    }

    /// <summary>
    /// Groups speech into processing windows: contiguous time spans of at most <see cref="MaxChunkMs"/> that contain
    /// speech; long silent stretches are skipped entirely (saves time and avoids hallucinated text in silence).
    /// </summary>
    /// <param name="speech">Speech signal.</param>
    /// <returns>Windows (start, end) in ms.</returns>
    public static List<(long Start, long End)> SpeechWindows(SpeechSignal speech)
    {
        const int PadMs = 300;
        const int MergeGapMs = 1500;
        var regions = new List<(long Start, long End)>();
        long? start = null;
        for (var t = 0; t <= speech.Length; t++)
        {
            var on = t < speech.Length && speech.Frames[t] >= 100;
            var ms = t * (long)SpeechSignal.FrameMs;
            if (on && start is null)
            {
                start = ms;
            }
            else if (!on && start is not null)
            {
                if (ms - start.Value >= 200)
                {
                    regions.Add((Math.Max(0, start.Value - PadMs), ms + PadMs));
                }

                start = null;
            }
        }

        var windows = new List<(long Start, long End)>();
        foreach (var r in regions)
        {
            if (windows.Count > 0)
            {
                var last = windows[^1];
                if (r.Start - last.End <= MergeGapMs && r.End - last.Start <= MaxChunkMs)
                {
                    windows[^1] = (last.Start, Math.Max(last.End, r.End));
                    continue;
                }
            }

            // Very long continuous speech is split into max-size pieces.
            for (var s = r.Start; s < r.End; s += MaxChunkMs)
            {
                windows.Add((s, Math.Min(r.End, s + MaxChunkMs)));
            }
        }

        return windows;
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
    /// <param name="progress">Progress 0..1.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Segments with absolute timestamps.</returns>
    public static async Task<List<WhisperSegment>> TranscribeAsync(
        string modelPath,
        string pcmPath,
        IReadOnlyList<(long Start, long End)> windows,
        string language,
        bool translateToEnglish,
        int threads,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        EnsureNativeLibrary();
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
        var result = new List<WhisperSegment>();
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
                    result.Add(new WhisperSegment(start + (long)seg.Start.TotalMilliseconds, start + (long)seg.End.TotalMilliseconds, text));
                }
            }

            progress?.Report((i + 1.0) / windows.Count);
        }

        return result;
    }
}
