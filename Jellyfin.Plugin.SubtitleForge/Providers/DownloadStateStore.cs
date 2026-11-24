using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
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
    /// <param name="reference">Source + reference of a saved download (to block it later).</param>
    public void Set(Guid itemId, string language, string outcome, string? detail, string? reference = null)
    {
        var entries = Load();
        var key = Key(itemId, language);
        DateTime? firstMissing = outcome == "saved" ? null : (entries.TryGetValue(key, out var old) ? old.FirstMissingUtc : null) ?? DateTime.UtcNow;
        entries[key] = new Entry { LastTriedUtc = DateTime.UtcNow, Outcome = outcome, Detail = detail, Reference = reference, FirstMissingUtc = firstMissing };
        Save();
    }

    /// <summary>
    /// Blocks a downloaded subtitle so it is never chosen again for this item and language.
    /// </summary>
    /// <param name="itemId">Item.</param>
    /// <param name="language">Language.</param>
    /// <param name="reference">Source + reference.</param>
    public void Block(Guid itemId, string language, string reference)
    {
        var key = Key(itemId, "block:" + language);
        var entries = Load();
        var existing = entries.TryGetValue(key, out var e) ? e.Detail ?? string.Empty : string.Empty;
        var set = new System.Collections.Generic.HashSet<string>(existing.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { reference };
        entries[key] = new Entry { LastTriedUtc = DateTime.UtcNow, Outcome = "blocked", Detail = string.Join('\n', set) };
        Save();
    }

    /// <summary>
    /// Whether a download is blocked.
    /// </summary>
    /// <param name="itemId">Item.</param>
    /// <param name="language">Language.</param>
    /// <param name="reference">Source + reference.</param>
    /// <returns>True if blocked.</returns>
    public bool IsBlocked(Guid itemId, string language, string reference) =>
        Get(itemId, "block:" + language)?.Detail?.Split('\n').Contains(reference) == true;

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

        /// <summary>Gets or sets "Source|Reference" of a saved download.</summary>
        public string? Reference { get; set; }

        /// <summary>Gets or sets since when no subtitle could be found (null once one was saved).</summary>
        public DateTime? FirstMissingUtc { get; set; }
    }
}
