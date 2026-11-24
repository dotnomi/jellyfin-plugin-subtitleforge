using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Sync;

/// <summary>
/// Extracts the speech signal of a video's main audio track (ffmpeg → 16 kHz mono PCM → Silero VAD) and caches it.
/// </summary>
public sealed class SpeechAnalyzer : IDisposable
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<SpeechAnalyzer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SpeechAnalyzer"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Media encoder.</param>
    /// <param name="appPaths">App paths.</param>
    /// <param name="logger">Logger.</param>
    public SpeechAnalyzer(IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger<SpeechAnalyzer> logger)
    {
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Runs VAD on raw PCM (s16le, 16 kHz, mono).
    /// </summary>
    /// <param name="pcm">PCM stream.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Signal.</returns>
    public static SpeechSignal AnalyzePcm(Stream pcm, CancellationToken cancellationToken)
    {
        using var vad = new SileroVad();
        var probs = new List<float>(1 << 16);
        var bytes = new byte[SileroVad.WindowSize * 2];
        var window = new float[SileroVad.WindowSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = 0;
            while (read < bytes.Length)
            {
                var n = pcm.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < bytes.Length)
            {
                break;
            }

            for (var i = 0; i < window.Length; i++)
            {
                window[i] = (short)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8)) / 32768f;
            }

            probs.Add(vad.Process(window));
        }

        return SpeechSignal.FromWindows(probs.ToArray());
    }

    /// <summary>
    /// Picks the audio stream subtitles are usually timed to: the default track, else the first one.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <returns>Stream or null.</returns>
    public static MediaStream? PickAudioStream(Video video)
    {
        var audio = video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).ToList();
        return audio.FirstOrDefault(s => s.IsDefault) ?? audio.FirstOrDefault();
    }

    /// <summary>
    /// Gets the cached signal for a video without analysing.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <returns>Signal or null.</returns>
    public SpeechSignal? TryGetCached(Video video)
    {
        var audio = PickAudioStream(video);
        return audio is null ? null : SpeechSignal.Load(CachePath(video, audio.Index));
    }

    /// <summary>
    /// Gets (or computes and caches) the speech signal of a video's main audio track.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Signal, or null if the video has no audio.</returns>
    public Task<SpeechSignal?> GetSignalAsync(Video video, CancellationToken cancellationToken)
    {
        var audio = PickAudioStream(video);
        return audio is null ? Task.FromResult<SpeechSignal?>(null) : GetSignalAsync(video, audio, cancellationToken)!;
    }

    /// <summary>
    /// Gets (or computes and caches) the speech signal of a specific audio track.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="audio">Audio stream.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Signal.</returns>
    public async Task<SpeechSignal?> GetSignalAsync(Video video, MediaStream audio, CancellationToken cancellationToken)
    {
        var cache = CachePath(video, audio.Index);
        var cached = SpeechSignal.Load(cache);
        if (cached is not null)
        {
            return cached;
        }

        // One analysis at a time: it is CPU heavy and NAS devices have few cores.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = SpeechSignal.Load(cache);
            if (cached is not null)
            {
                return cached;
            }

            var sw = Stopwatch.StartNew();
            using var process = StartFfmpeg(video, audio, "pipe:1");
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            SpeechSignal signal;
            try
            {
                signal = await Task.Run(() => AnalyzePcm(process.StandardOutput.BaseStream, cancellationToken), cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            if (process.ExitCode != 0 || signal.Length == 0)
            {
                throw new InvalidOperationException("Audio extraction failed: " + (await stderr.ConfigureAwait(false)).Trim());
            }

            signal.Save(cache);
            _logger.LogInformation(
                "SubtitleForge: speech analysis of {Path} took {Seconds:F1}s ({Minutes:F0} min audio, {Speech:P0} speech)",
                video.Path,
                sw.Elapsed.TotalSeconds,
                signal.Length * SpeechSignal.FrameMs / 60000.0,
                signal.SpeechRatio());
            return signal;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes an audio track as 16 kHz mono PCM (s16le) to a file and returns its speech signal (cached as well).
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="audio">Audio stream.</param>
    /// <param name="pcmPath">Output file.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Speech signal.</returns>
    public async Task<SpeechSignal> ExtractPcmAsync(Video video, MediaStream audio, string pcmPath, CancellationToken cancellationToken)
    {
        using (var process = StartFfmpeg(video, audio, pcmPath))
        {
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Audio extraction failed: " + (await stderr.ConfigureAwait(false)).Trim());
            }
        }

        var cache = CachePath(video, audio.Index);
        var signal = SpeechSignal.Load(cache);
        if (signal is null)
        {
            await using var fs = File.OpenRead(pcmPath);
            signal = await Task.Run(() => AnalyzePcm(fs, cancellationToken), cancellationToken).ConfigureAwait(false);
            signal.Save(cache);
        }

        return signal;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private Process StartFfmpeg(Video video, MediaStream audio, string output)
    {
        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Dialogue lives in the centre channel of surround mixes; music and effects in the others would mask it.
        var mix = audio.Channels >= 3
            ? new[] { "-af", "pan=mono|c0=FC" }
            : new[] { "-ac", "1" };
        var args = new List<string> { "-nostdin", "-v", "error", "-y", "-protocol_whitelist", "file", "-i", video.Path, "-map", StreamMap.Specifier(video.GetMediaStreams(), audio), "-vn", "-sn", "-dn" };
        args.AddRange(mix);
        args.AddRange(new[] { "-ar", "16000", "-f", "s16le", output });
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private string CachePath(Video video, int audioIndex)
    {
        var info = new FileInfo(video.Path);
        var key = string.Join('|', video.Path, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc.Ticks : 0, audioIndex, "v2");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 12).ToLowerInvariant();
        return Path.Combine(_appPaths.CachePath, "subtitleforge", "speech", hash[..2], hash + ".sig");
    }
}
