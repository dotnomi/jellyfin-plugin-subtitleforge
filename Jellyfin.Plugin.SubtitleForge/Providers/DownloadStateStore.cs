using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Remembers per item+language when the download job last tried and what happened,
/// so items without a good subtitle are not re-queried (and quota is not burned) every night.
/// </summary>
public class DownloadStateStore
{
    private readonly object _lock = new();
    private ConcurrentDictionary<string, Entry>? _entries;

    /// <summary>
    /// Gets an entry.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="language">Language.</param>
    /// <returns>Entry or null.</returns>
    public Entry? Get(Guid itemId, string language) =>
        Load().TryGetValue(Key(itemId, language), out var e) ? e : null;

    /// <summary>
    /// Sets an entry and persists.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="language">Language.</param>
    /// <param name="outcome">Outcome.</param>
    /// <param name="detail">Detail text.</param>
    public void Set(Guid itemId, string language, string outcome, string? detail)
    {
        Load()[Key(itemId, language)] = new Entry { LastTriedUtc = DateTime.UtcNow, Outcome = outcome, Detail = detail };
        Save();
    }

    private static string Key(Guid id, string lang) => id.ToString("N") + ":" + lang;

    private static string? FilePath()
    {
        var dir = Plugin.Instance?.DataFolderPath;
        return dir is null ? null : Path.Combine(dir, "download-state.json");
    }

    private ConcurrentDictionary<string, Entry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        lock (_lock)
        {
            if (_entries is not null)
            {
                return _entries;
            }

            var path = FilePath();
            try
            {
                _entries = path is not null && File.Exists(path)
                    ? JsonSerializer.Deserialize<ConcurrentDictionary<string, Entry>>(File.ReadAllText(path)) ?? new()
                    : new();
            }
            catch (JsonException)
            {
                _entries = new();
            }

            return _entries;
        }
    }

    private void Save()
    {
        var path = FilePath();
        if (path is null)
        {
            return;
        }

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_entries));
            File.Move(path + ".tmp", path, true);
        }
    }

    /// <summary>
    /// One remembered attempt.
    /// </summary>
    public sealed class Entry
    {
        /// <summary>Gets or sets the time of the last attempt.</summary>
        public DateTime LastTriedUtc { get; set; }

        /// <summary>Gets or sets the outcome (saved, no-safe-match, none-found, error).</summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>Gets or sets details.</summary>
        public string? Detail { get; set; }
    }
}
