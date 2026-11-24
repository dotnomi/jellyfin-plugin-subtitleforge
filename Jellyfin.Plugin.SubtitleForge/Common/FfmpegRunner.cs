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
        // Only local files as input: a media file that is really a playlist must not make ffmpeg fetch URLs.
        foreach (var a in new[] { "-nostdin", "-v", "error", "-y", "-protocol_whitelist", "file", "-i", input, "-map", streamSpecifier, "-c", "copy", "-f", format, output })
        {
            psi.ArgumentList.Add(a);
        }

        return RunAsync(psi, cancellationToken);
    }

    /// <summary>
    /// Reads the codec private data of a stream as text (for DVD subtitles: the VobSub ".idx" header with size and
    /// palette), using ffprobe.
    /// </summary>
    /// <param name="input">Input media path.</param>
    /// <param name="streamSpecifier">ffmpeg stream specifier ("0:s:1"), see <see cref="StreamMap"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Text, or null if the stream has none.</returns>
    public async Task<string?> ReadExtradataTextAsync(string input, string streamSpecifier, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(_mediaEncoder.ProbePath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var select = streamSpecifier.StartsWith("0:", StringComparison.Ordinal) ? streamSpecifier[2..] : streamSpecifier;
        foreach (var a in new[] { "-v", "error", "-protocol_whitelist", "file", "-show_streams", "-show_data", "-select_streams", select, input })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);
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

        return ParseExtradata(await stdout.ConfigureAwait(false));
    }

    /// <summary>
    /// Extracts the "extradata=" hex dump of ffprobe's -show_data output as text.
    /// </summary>
    /// <param name="probeOutput">ffprobe output.</param>
    /// <returns>Text, or null.</returns>
    public static string? ParseExtradata(string probeOutput)
    {
        var lines = probeOutput.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("extradata=", StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        var bytes = new System.Collections.Generic.List<byte>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            // "00000010: 6c65 7474 653a 2030 3030 3030 302c 2031  lette: 000000, 1"
            var line = lines[i];
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon != 8)
            {
                break;
            }

            var end = line.IndexOf("  ", colon + 2, StringComparison.Ordinal);
            var hex = (end < 0 ? line[(colon + 2)..] : line[(colon + 2)..end]).Replace(" ", string.Empty, StringComparison.Ordinal);
            for (var k = 0; k + 1 < hex.Length; k += 2)
            {
                bytes.Add(Convert.ToByte(hex.Substring(k, 2), 16));
            }
        }

        return bytes.Count == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes.ToArray());
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
