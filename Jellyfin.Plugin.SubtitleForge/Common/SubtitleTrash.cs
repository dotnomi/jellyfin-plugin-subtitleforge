using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Recycle bin for subtitles the user deleted or rejected: the file moves into the plugin's data folder and can be
/// put back for a few days (setting "TrashDays"). Only files SubtitleForge made ever get here.
/// </summary>
public static class SubtitleTrash
{
    /// <summary>Largest file that is kept (bigger ones are deleted right away).</summary>
    public const long MaxFileBytes = 20_000_000;

    private const int MaxEntries = 2000;
    private const long MaxTotalBytes = 300_000_000;
    private static readonly object Lock = new();

    /// <summary>Gets or sets the storage folder (tests; default: "trash" in the plugin's data folder).</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>Gets or sets the clock (tests).</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private static int Days => Math.Clamp(Plugin.Instance?.Configuration.TrashDays ?? 30, 0, 365);

    /// <summary>
    /// Moves a file into the bin. When the bin is off, full or the file too big, the file is deleted instead.
    /// </summary>
    /// <param name="itemId">Video id.</param>
    /// <param name="path">Subtitle file.</param>
    /// <param name="reason">"deleted" or "rejected".</param>
    /// <param name="states">Plugin state before the deletion (restored with the file).</param>
    /// <returns>The new entry, or null when the file was deleted for good.</returns>
    /// <exception cref="IOException">The file could neither be moved nor deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">No permission.</exception>
    public static Entry? Remove(Guid itemId, string path, string reason, IReadOnlyList<StateSnapshot>? states = null)
    {
        var folder = Folder();
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            size = long.MaxValue;
        }

        // A link is removed, never kept: restoring it would copy whatever it points to next to the video.
        if (folder is null || Days == 0 || size > MaxFileBytes || new FileInfo(path).LinkTarget is not null)
        {
            File.Delete(path);
            return null;
        }

        lock (Lock)
        {
            var entries = PurgeLocked(Load(folder), folder);
            while (entries.Count >= MaxEntries || (entries.Count > 0 && entries.Sum(e => e.Size) + size > MaxTotalBytes))
            {
                DeleteStored(folder, entries[0]);
                entries.RemoveAt(0);
            }

            var entry = new Entry
            {
                Id = Guid.NewGuid().ToString("N"),
                ItemId = itemId,
                OriginalPath = path,
                FileName = Path.GetFileName(path),
                DeletedUtc = UtcNow(),
                Reason = reason,
                Size = size,
                States = states?.ToList() ?? new List<StateSnapshot>(),
            };
            Directory.CreateDirectory(folder);
            try
            {
                File.Move(path, StoredPath(folder, entry.Id));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // E.g. no permission to create files in the data folder: behave like before (plain delete).
                File.Delete(path);
                return null;
            }

            entries.Add(entry);
            Save(folder, entries);
            return entry;
        }
    }

    /// <summary>
    /// Entries of one video (or all), newest first.
    /// </summary>
    /// <param name="itemId">Video id, null = all.</param>
    /// <returns>Entries.</returns>
    public static IReadOnlyList<Entry> List(Guid? itemId)
    {
        if (Folder() is not { } folder)
        {
            return Array.Empty<Entry>();
        }

        lock (Lock)
        {
            var entries = PurgeLocked(Load(folder), folder);
            return entries.Where(e => itemId is null || e.ItemId == itemId).OrderByDescending(e => e.DeletedUtc).ToList();
        }
    }

    /// <summary>
    /// Reads a stored file.
    /// </summary>
    /// <param name="id">Entry id.</param>
    /// <returns>Entry and content, or null.</returns>
    public static (Entry Entry, byte[] Content)? Read(string id)
    {
        if (Folder() is not { } folder || !IsId(id))
        {
            return null;
        }

        lock (Lock)
        {
            var entry = Load(folder).FirstOrDefault(e => e.Id == id);
            var stored = StoredPath(folder, id);
            var info = new FileInfo(stored);
            if (entry is null || !info.Exists || info.LinkTarget is not null || info.Length > MaxFileBytes)
            {
                return null;
            }

            return (entry, File.ReadAllBytes(stored));
        }
    }

    /// <summary>
    /// Removes an entry and its stored file (after a restore).
    /// </summary>
    /// <param name="id">Entry id.</param>
    public static void Forget(string id)
    {
        if (Folder() is not { } folder || !IsId(id))
        {
            return;
        }

        lock (Lock)
        {
            var entries = Load(folder);
            var entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                DeleteStored(folder, entry);
                entries.Remove(entry);
                Save(folder, entries);
            }
        }
    }

    /// <summary>
    /// Deletes entries older than the configured number of days.
    /// </summary>
    public static void Purge()
    {
        if (Folder() is not { } folder)
        {
            return;
        }

        lock (Lock)
        {
            PurgeLocked(Load(folder), folder);
        }
    }

    private static bool IsId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    private static string? Folder() =>
        FolderOverride ?? (Plugin.Instance?.DataFolderPath is { } d ? Path.Combine(d, "trash") : null);

    private static string StoredPath(string folder, string id) => Path.Combine(folder, id + ".srt");

    private static void DeleteStored(string folder, Entry entry)
    {
        try
        {
            File.Delete(StoredPath(folder, entry.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<Entry> PurgeLocked(List<Entry> entries, string folder)
    {
        var limit = UtcNow().AddDays(-Days);
        var old = entries.Where(e => e.DeletedUtc < limit || !IsId(e.Id)).ToList();
        if (old.Count == 0)
        {
            return entries;
        }

        foreach (var e in old.Where(e => IsId(e.Id)))
        {
            DeleteStored(folder, e);
        }

        entries.RemoveAll(old.Contains);
        Save(folder, entries);
        return entries;
    }

    private static List<Entry> Load(string folder)
    {
        var path = Path.Combine(folder, "index.json");
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>()).OrderBy(e => e.DeletedUtc).ToList()
                : new List<Entry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<Entry>();
        }
    }

    private static void Save(string folder, List<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "index.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// One deleted file.
    /// </summary>
    public sealed class Entry
    {
        /// <summary>Gets or sets the entry id (32 hex characters).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the video id.</summary>
        public Guid ItemId { get; set; }

        /// <summary>Gets or sets where the file was.</summary>
        public string OriginalPath { get; set; } = string.Empty;

        /// <summary>Gets or sets the file name.</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>Gets or sets when it was deleted.</summary>
        public DateTime DeletedUtc { get; set; }

        /// <summary>Gets or sets why ("deleted", "rejected").</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Gets or sets the file size.</summary>
        public long Size { get; set; }

        /// <summary>Gets or sets the plugin state before the deletion.</summary>
#pragma warning disable CA2227 // JSON
        public List<StateSnapshot> States { get; set; } = new();
#pragma warning restore CA2227
    }

    /// <summary>
    /// A remembered entry of the download state.
    /// </summary>
    public sealed class StateSnapshot
    {
        /// <summary>Gets or sets the state key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Gets or sets the outcome (null = there was none).</summary>
        public string? Outcome { get; set; }

        /// <summary>Gets or sets the detail.</summary>
        public string? Detail { get; set; }

        /// <summary>Gets or sets the reference.</summary>
        public string? Reference { get; set; }
    }
}
