using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Runs Jellyfin's own ffmpeg binary.
/// </summary>
public class FfmpegRunner
{
    private readonly IMediaEncoder _mediaEncoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegRunner"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Media encoder (provides the ffmpeg path).</param>
    public FfmpegRunner(IMediaEncoder mediaEncoder)
    {
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Copies one stream of a media file without re-encoding.
    /// </summary>
    /// <param name="input">Input media path.</param>
    /// <param name="streamSpecifier">ffmpeg stream specifier, see <see cref="StreamMap"/>.</param>
    /// <param name="format">Output muxer, e.g. "sup".</param>
    /// <param name="output">Output path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public Task ExtractStreamAsync(string input, string streamSpecifier, string format, string output, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-nostdin", "-v", "error", "-y", "-i", input, "-map", streamSpecifier, "-c", "copy", "-f", format, output })
        {
            psi.ArgumentList.Add(a);
        }

        return RunAsync(psi, cancellationToken);
    }

    private static async Task RunAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var err = await stderrTask.ConfigureAwait(false);
            throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {err.Trim()}");
        }
    }
}
