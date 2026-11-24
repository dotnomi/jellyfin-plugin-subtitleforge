using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Whisper;

/// <summary>
/// Briefly runs Whisper on the GPU after Jellyfin started (and on demand), so a broken GPU driver shows up in the
/// settings right away instead of during the first real job. Catches missing/broken Vulkan drivers and crashes;
/// rare hangs that only appear after minutes of work are still handled by the CPU fallback of each job.
/// </summary>
public sealed class GpuSelfTest : IHostedService, IDisposable
{
    private readonly WhisperModels _models;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<GpuSelfTest> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuSelfTest"/> class.
    /// </summary>
    /// <param name="models">Model store.</param>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public GpuSelfTest(WhisperModels models, IApplicationPaths paths, ILogger<GpuSelfTest> logger)
    {
        _models = models;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Gets the result of the last test (null = not run yet).</summary>
    public static string? LastResult { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(3), _stop.Token).ConfigureAwait(false);
                    if (Plugin.Instance?.Configuration is { GpuSelfTest: true, WhisperUseGpu: true, WhisperEnabled: true })
                    {
                        await RunAsync(false, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SubtitleForge: GPU self-test failed");
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _stop.CancelAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Runs the test.
    /// </summary>
    /// <param name="downloadModel">Download the configured model if it is missing (manual test); otherwise skip.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Human-readable result.</returns>
    public async Task<string> RunAsync(bool downloadModel, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = Plugin.Instance?.Configuration;
            var helper = WhisperEngine.HelperPath();
            if (helper is null)
            {
                return LastResult = Loc.T("Self-test not possible: Whisper helper is missing for this platform", "Selbsttest nicht möglich: Whisper-Helfer fehlt für diese Plattform");
            }

            if (!WhisperEngine.WantGpu())
            {
                return LastResult = Loc.T("no usable GPU: ", "keine nutzbare GPU: ") + WhisperEngine.GpuStatus;
            }

            var model = WhisperModels.Get(config?.WhisperModel);
            var dir = Path.Combine(Plugin.Instance?.DataFolderPath ?? _paths.TempDirectory, "whisper-models");
            if (!downloadModel && !File.Exists(Path.Combine(dir, model.File)))
            {
                return LastResult = Loc.T("Self-test skipped: Whisper model not downloaded yet", "Selbsttest übersprungen: Whisper-Modell noch nicht heruntergeladen");
            }

            var modelPath = await _models.EnsureAsync(model, dir, cancellationToken).ConfigureAwait(false);
            var tempDir = Path.Combine(_paths.TempDirectory, "subtitleforge");
            Directory.CreateDirectory(tempDir);
            var pcm = Path.Combine(tempDir, "selftest-" + Guid.NewGuid().ToString("N") + ".pcm");
            try
            {
                const int Seconds = 168; // six 28 s windows: several full encoder passes on the GPU
                await File.WriteAllBytesAsync(pcm, SyntheticAudio(Seconds), cancellationToken).ConfigureAwait(false);
                var windows = new List<(long Start, long End)>();
                for (long s = 0; s < Seconds * 1000L; s += WhisperEngine.MaxChunkMs)
                {
                    windows.Add((s, Math.Min(Seconds * 1000L, s + WhisperEngine.MaxChunkMs)));
                }

                var sw = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                await WhisperEngine.TranscribeAsync(modelPath, pcm, windows, "de", false, 2, null, timeout.Token).ConfigureAwait(false);
                var result = WhisperEngine.GpuBroken
                    ? Loc.T("GPU self-test failed – Whisper uses the CPU: ", "GPU-Selbsttest fehlgeschlagen – Whisper nutzt die CPU: ") + WhisperEngine.GpuStatus
                    : Loc.German
                        ? FormattableString.Invariant($"GPU-Selbsttest bestanden ({WhisperEngine.LoadedRuntime}, {sw.Elapsed.TotalSeconds:F0} s für {Seconds} s Testton)")
                        : FormattableString.Invariant($"GPU self-test passed ({WhisperEngine.LoadedRuntime}, {sw.Elapsed.TotalSeconds:F0} s for {Seconds} s of test audio)");
                _logger.LogInformation("SubtitleForge: {Result}", result);
                return LastResult = result;
            }
            finally
            {
                try
                {
                    File.Delete(pcm);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 16 kHz mono s16le: modulated tones and noise (keeps the encoder busy like real audio).
    /// </summary>
    /// <param name="seconds">Length.</param>
    /// <returns>PCM bytes.</returns>
    internal static byte[] SyntheticAudio(int seconds)
    {
        var n = seconds * 16000;
        var bytes = new byte[n * 2];
        var rng = new Random(42);
        for (var i = 0; i < n; i++)
        {
            var t = i / 16000.0;
            var f = 180 + (120 * Math.Sin(t * 0.7));
            var v = (0.25 * Math.Sin(2 * Math.PI * f * t) * (0.5 + (0.5 * Math.Sin(t * 3)))) + (0.05 * ((rng.NextDouble() * 2) - 1));
            var sample = (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)sample;
            bytes[(2 * i) + 1] = (byte)(sample >> 8);
        }

        return bytes;
    }
}
