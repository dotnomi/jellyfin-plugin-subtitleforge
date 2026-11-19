using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Remembers which sources hit their daily limit and until when, so a library-wide run pauses that source and
/// simply continues the next night instead of marking the remaining items as failed.
/// </summary>
public class QuotaGuard
{
    private readonly ConcurrentDictionary<string, DateTime> _pausedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pauses a source until its quota resets (default: next midnight UTC).
    /// </summary>
    /// <param name="source">Source name.</param>
    /// <param name="resetUtc">Reset time reported by the source.</param>
    public void Pause(string source, DateTime? resetUtc)
    {
        var until = resetUtc is { } r && r > DateTime.UtcNow ? r : DateTime.UtcNow.Date.AddDays(1);
        _pausedUntil[source] = until;
    }

    /// <summary>
    /// Whether a source is currently paused.
    /// </summary>
    /// <param name="source">Source name.</param>
    /// <returns>True if paused.</returns>
    public bool IsPaused(string source) => _pausedUntil.TryGetValue(source, out var until) && until > DateTime.UtcNow;

    /// <summary>
    /// When a paused source becomes available again.
    /// </summary>
    /// <param name="source">Source name.</param>
    /// <returns>UTC time or null.</returns>
    public DateTime? PausedUntil(string source) => IsPaused(source) ? _pausedUntil[source] : null;
}
