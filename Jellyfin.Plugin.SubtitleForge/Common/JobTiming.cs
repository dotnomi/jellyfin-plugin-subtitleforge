using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Learns how long jobs take (moving average, persisted) to estimate remaining time. Whisper is measured in seconds
/// per minute of video, everything else in seconds per job.
/// </summary>
public class JobTiming
{
    private static readonly Dictionary<string, double> Defaults = new(StringComparer.Ordinal)
    {
        ["Whisper"] = 8,   // s per minute of video (measured: CPU 6–9, GPU 3–7)
        ["Search"] = 40,
        ["NewItem"] = 90,
        ["Translate"] = 60,
    };

    private readonly object _lock = new();
    private Dictionary<string, double>? _values;

    /// <summary>
    /// Estimated duration of a job.
    /// </summary>
    /// <param name="kind">Job kind.</param>
    /// <param name="runtimeMinutes">Length of the video in minutes (for Whisper).</param>
    /// <returns>Seconds.</returns>
    public double Estimate(string kind, double runtimeMinutes)
    {
        var v = Get(kind);
        return kind == "Whisper" ? v * Math.Max(1, runtimeMinutes) : v;
    }

    /// <summary>
    /// Records a finished job.
    /// </summary>
    /// <param name="kind">Job kind.</param>
    /// <param name="seconds">Duration.</param>
    /// <param name="runtimeMinutes">Length of the video in minutes.</param>
    public void Record(string kind, double seconds, double runtimeMinutes)
    {
        var value = kind == "Whisper" ? seconds / Math.Max(1, runtimeMinutes) : seconds;
        lock (_lock)
        {
            var values = Load();
            values[kind] = values.TryGetValue(kind, out var old) ? (0.7 * old) + (0.3 * value) : value;
            Save(values);
        }
    }

    private double Get(string kind)
    {
        lock (_lock)
        {
            return Load().TryGetValue(kind, out var v) ? v : Defaults.GetValueOrDefault(kind, 60);
        }
    }

    private static string? FilePath() => Plugin.Instance is { } p ? Path.Combine(p.DataFolderPath, "job-timing.json") : null;

    private Dictionary<string, double> Load()
    {
        if (_values is not null)
        {
            return _values;
        }

        try
        {
            _values = FilePath() is { } path && File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) ?? new Dictionary<string, double>()
                : new Dictionary<string, double>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _values = new Dictionary<string, double>();
        }

        return _values;
    }

    private static void Save(Dictionary<string, double> values)
    {
        try
        {
            if (FilePath() is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(values));
            }
        }
        catch (IOException)
        {
        }
    }
}
