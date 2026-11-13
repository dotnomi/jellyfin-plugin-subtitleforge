using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Sync;

/// <summary>
/// Verifies and corrects subtitle timing against a video's audio.
/// </summary>
public class SyncService
{
    private readonly SpeechAnalyzer _analyzer;
    private readonly ILogger<SyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncService"/> class.
    /// </summary>
    /// <param name="analyzer">Speech analyzer.</param>
    /// <param name="logger">Logger.</param>
    public SyncService(SpeechAnalyzer analyzer, ILogger<SyncService> logger)
    {
        _analyzer = analyzer;
        _logger = logger;
    }

    /// <summary>
    /// Aligns cues to the video's speech. Analyses the audio on first use (cached afterwards).
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="cues">Cues.</param>
    /// <param name="cachedOnly">Only use an existing speech analysis (fast path for interactive requests).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Result, or null if the audio could not be analysed.</returns>
    public async Task<AlignmentResult?> AlignAsync(Video video, IReadOnlyList<SubtitleCue> cues, bool cachedOnly, CancellationToken cancellationToken)
    {
        SpeechSignal? speech;
        try
        {
            speech = cachedOnly ? _analyzer.TryGetCached(video) : await _analyzer.GetSignalAsync(video, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SubtitleForge: speech analysis failed for {Path}", video.Path);
            return null;
        }

        if (speech is null || speech.SpeechRatio() < 0.02)
        {
            return null; // no audio / no detectable speech: cannot verify
        }

        return await Task.Run(() => SubtitleAligner.Align(cues, speech), cancellationToken).ConfigureAwait(false);
    }
}
