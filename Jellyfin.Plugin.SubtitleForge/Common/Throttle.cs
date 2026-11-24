using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Keeps heavy work out of the way: pauses while someone is watching, and limits automatic work to a time window.
/// </summary>
public class Throttle
{
    private readonly ISessionManager _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="Throttle"/> class.
    /// </summary>
    /// <param name="sessions">Session manager.</param>
    public Throttle(ISessionManager sessions)
    {
        _sessions = sessions;
        Whisper.WhisperEngine.PauseRequested = ShouldPause;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether someone is currently playing something (paused playback does not count).
    /// </summary>
    public bool IsPlaybackActive => _sessions.Sessions.Any(s => s.NowPlayingItem is not null && s.PlayState is { IsPaused: false });

    /// <summary>
    /// Whether heavy work should pause right now.
    /// </summary>
    /// <returns>True while someone watches and pausing is enabled.</returns>
    public bool ShouldPause() => Config.PauseDuringPlayback && IsPlaybackActive;

    /// <summary>
    /// Whether the given time lies inside the window for automatic work.
    /// </summary>
    /// <param name="now">Local time.</param>
    /// <param name="config">Configuration.</param>
    /// <returns>True if automatic work may run.</returns>
    public static bool InWindow(DateTime now, PluginConfiguration config)
    {
        if (!config.AutoWorkWindowEnabled)
        {
            return true;
        }

        int start = Math.Clamp(config.AutoWorkStartHour, 0, 23), end = Math.Clamp(config.AutoWorkEndHour, 0, 24);
        var h = now.Hour;
        return start == end || (start < end ? h >= start && h < end : h >= start || h < end);
    }

    /// <summary>
    /// Waits while someone is watching (if enabled).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public async Task WaitForPlaybackAsync(CancellationToken cancellationToken)
    {
        while (ShouldPause())
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until automatic work is allowed (time window) and nobody is watching.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public async Task WaitForAutomaticWorkAsync(CancellationToken cancellationToken)
    {
        while (!InWindow(DateTime.Now, Config))
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        }

        await WaitForPlaybackAsync(cancellationToken).ConfigureAwait(false);
    }
}
