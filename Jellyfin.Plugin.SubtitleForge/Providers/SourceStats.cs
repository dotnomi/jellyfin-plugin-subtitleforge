using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Learns which source fits this library: every saved download counts for its source, every "doesn't fit" or
/// failed audio check against it. The ranking gets a small bonus or malus from that, never enough to beat a hash
/// or release group match.
/// </summary>
public static class SourceStats
{
    /// <summary>Largest bonus or malus a source can get.</summary>
    public const int MaxBonus = 25;

    private static readonly object Lock = new();
    private static Dictionary<string, Counts>? _values;

    /// <summary>Gets or sets the storage folder (tests; default: the plugin's data folder).</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>
    /// A download of this source was saved.
    /// </summary>
    /// <param name="source">Source name.</param>
    public static void Saved(string source) => Change(source, c => c.Saved++);

    /// <summary>
    /// A download of this source was rejected by the user or did not match the audio.
    /// </summary>
    /// <param name="source">Source name.</param>
    public static void Failed(string source) => Change(source, c => c.Failed++);

    /// <summary>
    /// Ranking bonus per source.
    /// </summary>
    /// <returns>Source name → points.</returns>
    public static IReadOnlyDictionary<string, int> Bonuses()
    {
        lock (Lock)
        {
            return Load().ToDictionary(kv => kv.Key, kv => Bonus(kv.Value), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Current counters for display.
    /// </summary>
    /// <returns>Rows.</returns>
    public static IReadOnlyList<(string Source, int Saved, int Failed, int Bonus)> All()
    {
        lock (Lock)
        {
            return Load().OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value.Saved, kv.Value.Failed, Bonus(kv.Value)))
                .ToList();
        }
    }

    /// <summary>
    /// Points for counters: success rate around the neutral 50 %, damped while there is little data.
    /// </summary>
    /// <param name="c">Counters.</param>
    /// <returns>Points between -<see cref="MaxBonus"/> and <see cref="MaxBonus"/>.</returns>
    internal static int Bonus(Counts c)
    {
        var total = c.Saved + c.Failed;
        if (total == 0)
        {
            return 0;
        }

        var rate = (double)c.Saved / total;
        var confidence = total / (total + 10.0);
        return (int)Math.Round(Math.Clamp((rate - 0.5) * 2 * MaxBonus * confidence, -MaxBonus, MaxBonus));
    }

    internal static void Reset()
    {
        lock (Lock)
        {
            _values = null;
        }
    }

    private static void Change(string source, Action<Counts> change)
    {
        if (string.IsNullOrEmpty(source) || source.Length > 40)
        {
            return;
        }

        lock (Lock)
        {
            var values = Load();
            if (!values.TryGetValue(source, out var c))
            {
                if (values.Count >= 50)
                {
                    return;
                }

                c = new Counts();
                values[source] = c;
            }

            change(c);
            Save(values);
        }
    }

    private static string? FilePath()
    {
        var dir = FolderOverride ?? Plugin.Instance?.DataFolderPath;
        return dir is null ? null : Path.Combine(dir, "source-stats.json");
    }

    private static Dictionary<string, Counts> Load()
    {
        if (_values is not null)
        {
            return _values;
        }

        try
        {
            _values = FilePath() is { } path && File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Counts>>(File.ReadAllText(path)) ?? new Dictionary<string, Counts>()
                : new Dictionary<string, Counts>();
            _values = new Dictionary<string, Counts>(_values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            _values = new Dictionary<string, Counts>(StringComparer.OrdinalIgnoreCase);
        }

        return _values;
    }

    private static void Save(Dictionary<string, Counts> values)
    {
        try
        {
            if (FilePath() is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(values));
                File.Move(path + ".tmp", path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Counters of one source.
    /// </summary>
    public sealed class Counts
    {
        /// <summary>Gets or sets the number of saved downloads.</summary>
        public int Saved { get; set; }

        /// <summary>Gets or sets the number of rejected or mismatching downloads.</summary>
        public int Failed { get; set; }
    }
}
