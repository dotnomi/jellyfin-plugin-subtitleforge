using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Sync;

namespace Jellyfin.Plugin.SubtitleForge.Whisper;

/// <summary>
/// Runs whisper.cpp over the speech regions of an audio track. Normally in a separate helper process
/// (SubtitleForge.WhisperHost): a crash of the GPU driver or of whisper.cpp then only fails this job instead of
/// taking down Jellyfin, and the job is retried on the CPU.
/// </summary>
public static class WhisperEngine
{
    /// <summary>Max length of one processing window (whisper works in 30 s windows).</summary>
    public const int MaxChunkMs = WhisperCore.MaxChunkMs;

    /// <summary>Gives up on a helper that has not reported anything for this long (hung driver).</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(10);

    private static int _configured;
    private static string? _helperRuntime;
    private static bool _gpuBroken;
    private static string? _gpuStatus;

    /// <summary>Gets or sets a check whether the helper should be paused right now (someone is watching).</summary>
    public static Func<bool>? PauseRequested { get; set; }

    /// <summary>Gets or sets a sink for diagnostic messages (set by the plugin to its logger).</summary>
    public static Action<string>? Diagnostics { get; set; }

    /// <summary>
    /// Gets the runtime whisper.cpp ran on last ("Vulkan" = GPU, "Cpu").
    /// </summary>
    public static string LoadedRuntime => _helperRuntime ?? WhisperCore.LoadedRuntime ?? Loc.T("not loaded yet", "noch nicht geladen");

    /// <summary>Gets a value indicating whether Whisper crashed on the GPU in this Jellyfin session.</summary>
    public static bool GpuBroken => _gpuBroken;

    /// <summary>Gets the result of the GPU check (why Vulkan is or is not used).</summary>
    public static string GpuStatus
    {
        get => _gpuStatus ?? Loc.T("not checked yet", "noch nicht geprüft");
        private set => _gpuStatus = value;
    }

    private static string PluginDirectory =>
        Path.GetDirectoryName(typeof(WhisperEngine).Assembly.Location) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Decides whether the GPU (Vulkan) should be tried.
    /// </summary>
    /// <returns>True for GPU.</returns>
    public static bool WantGpu()
    {
        var useGpu = Environment.GetEnvironmentVariable("SF_WHISPER_GPU") is { } env
            ? env == "1"
            : Plugin.Instance?.Configuration.WhisperUseGpu ?? true;
        if (!useGpu)
        {
            GpuStatus = Loc.T("GPU acceleration turned off", "GPU-Beschleunigung ausgeschaltet");
            return false;
        }

        if (_gpuBroken)
        {
            return false;
        }

        var gpu = GpuDetector.Check();
        GpuStatus = gpu.Description;
        return gpu.Usable;
    }

    /// <summary>
    /// Configures the in-process native library (fallback when the helper is not available).
    /// </summary>
    public static void EnsureNativeLibrary()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        WhisperCore.ConfigureNative(typeof(WhisperEngine).Assembly.Location, WantGpu());
    }

    /// <summary>
    /// Path of the helper executable for this platform, or null if it is not shipped.
    /// </summary>
    /// <returns>Path or null.</returns>
    public static string? HelperPath()
    {
        if (Environment.GetEnvironmentVariable("SF_WHISPER_INPROC") == "1" || !OperatingSystem.IsLinux())
        {
            return null;
        }

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null,
        };
        if (rid is null)
        {
            return null;
        }

        var path = Path.Combine(PluginDirectory, "whisperhost", rid, "sf-whisper");
        if (!File.Exists(path))
        {
            return null;
        }

        // Zip extraction does not always keep the executable bit.
        try
        {
            var mode = File.GetUnixFileMode(path);
            if (!mode.HasFlag(UnixFileMode.UserExecute))
            {
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics?.Invoke(Loc.T("Whisper helper not executable: ", "Whisper-Helfer nicht ausführbar: ") + ex.Message);
            return null;
        }

        return path;
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
        var helper = HelperPath();
        if (helper is null)
        {
            EnsureNativeLibrary();
            var result = new List<WhisperSegment>();
            await WhisperCore.TranscribeAsync(modelPath, pcmPath, windows, language, translateToEnglish, threads, result.Add, progress, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var gpu = WantGpu();
        try
        {
            return await RunHelperAsync(helper, modelPath, pcmPath, windows, language, translateToEnglish, threads, gpu, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (WhisperHelperException ex) when (gpu && !cancellationToken.IsCancellationRequested)
        {
            // GPU driver/whisper.cpp crashed on the GPU: Jellyfin keeps running; use the CPU from now on.
            _gpuBroken = true;
            GpuStatus = Loc.T("GPU error in Whisper (", "GPU-Fehler bei Whisper (") + ex.Message + Loc.T(") – the CPU is used until the next Jellyfin restart", ") – bis zum nächsten Jellyfin-Neustart wird die CPU genutzt");
            Diagnostics?.Invoke(Loc.T("Whisper aborted on the GPU, retrying on the CPU: ", "Whisper auf der GPU abgebrochen, wiederhole auf der CPU: ") + ex.Message);
            progress?.Report(0);
            return await RunHelperAsync(helper, modelPath, pcmPath, windows, language, translateToEnglish, threads, false, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<WhisperSegment>> RunHelperAsync(
        string helper,
        string modelPath,
        string pcmPath,
        IReadOnlyList<(long Start, long End)> windows,
        string language,
        bool translateToEnglish,
        int threads,
        bool gpu,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var windowFile = pcmPath + ".windows";
        await File.WriteAllLinesAsync(
            windowFile,
            windows.Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Start} {w.End}")),
            cancellationToken).ConfigureAwait(false);

        var psi = new ProcessStartInfo(helper)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "--lib", typeof(WhisperEngine).Assembly.Location,
            "--model", modelPath,
            "--pcm", pcmPath,
            "--windows", windowFile,
            "--lang", language,
            "--threads", threads.ToString(CultureInfo.InvariantCulture),
            "--gpu", gpu ? "1" : "0",
            "--translate", translateToEnglish ? "1" : "0",
        })
        {
            psi.ArgumentList.Add(a);
        }

        var result = new List<WhisperSegment>();
        var stderr = new Queue<string>();
        var lastActivity = DateTime.UtcNow;
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
            var errTask = Task.Run(
                async () =>
                {
                    while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                    {
                        lock (stderr)
                        {
                            stderr.Enqueue(line);
                            if (stderr.Count > 20)
                            {
                                stderr.Dequeue();
                            }
                        }
                    }
                },
                CancellationToken.None);

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var paused = false;
            var watchdog = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!stall.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), stall.Token).ConfigureAwait(false);

                            // Someone is watching: freeze the helper (SIGSTOP) and continue afterwards (SIGCONT).
                            var pause = PauseRequested?.Invoke() ?? false;
                            if (pause != paused && Signal(process, pause ? SigStop : SigCont))
                            {
                                paused = pause;
                                Diagnostics?.Invoke(pause ? Loc.T("Whisper paused (playback running)", "Whisper pausiert (Wiedergabe läuft)") : Loc.T("Whisper resumed", "Whisper läuft weiter"));
                            }

                            if (paused)
                            {
                                lastActivity = DateTime.UtcNow;
                            }
                            else if (DateTime.UtcNow - lastActivity > StallTimeout)
                            {
                                Kill(process);
                                return;
                            }
                        }
                    }
                    finally
                    {
                        if (paused)
                        {
                            Signal(process, SigCont);
                        }
                    }
                },
                CancellationToken.None);

            using (cancellationToken.Register(() => Kill(process)))
            {
                while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                {
                    lastActivity = DateTime.UtcNow;
                    if (line.StartsWith("S ", StringComparison.Ordinal))
                    {
                        var p = line.Split(' ', 4);
                        result.Add(new WhisperSegment(
                            long.Parse(p[1], CultureInfo.InvariantCulture),
                            long.Parse(p[2], CultureInfo.InvariantCulture),
                            Uri.UnescapeDataString(p[3])));
                    }
                    else if (line.StartsWith("P ", StringComparison.Ordinal))
                    {
                        progress?.Report(double.Parse(line[2..], CultureInfo.InvariantCulture));
                    }
                    else if (line.StartsWith("R ", StringComparison.Ordinal))
                    {
                        _helperRuntime = line[2..];
                    }
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await stall.CancelAsync().ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                string tail;
                lock (stderr)
                {
                    // Skip .NET stack frames; keep messages like "what(): vk::Device::waitForFences: ErrorDeviceLost".
                    tail = string.Join(" | ", stderr.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("at ", StringComparison.Ordinal)).TakeLast(2));
                }

                var reason = DateTime.UtcNow - lastActivity > StallTimeout
                    ? Loc.T("stopped responding", "keine Rückmeldung mehr")
                    : (Loc.German ? "Exitcode " : "exit code ") + process.ExitCode.ToString(CultureInfo.InvariantCulture);
                throw new WhisperHelperException(tail.Length > 0 ? $"{reason}: {tail}" : reason);
            }

            return result;
        }
        finally
        {
            Kill(process);
            try
            {
                File.Delete(windowFile);
            }
            catch (IOException)
            {
            }
        }
    }

    private const int SigCont = 18;
    private const int SigStop = 19;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);

    private static bool Signal(Process process, int sig)
    {
        try
        {
            return !process.HasExited && SysKill(process.Id, sig) == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void Kill(Process process)
    {
        Signal(process, SigCont);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}

/// <summary>
/// The Whisper helper process failed (crash, driver error, hang).
/// </summary>
public sealed class WhisperHelperException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WhisperHelperException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    public WhisperHelperException(string message)
        : base(message)
    {
    }
}
