using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Providers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Counts what SubtitleForge created during the week and sends a summary once a week (setting "WeeklyReport"):
/// new subtitles per kind and the titles where nothing fitting was found. Also empties old recycle bin entries.
/// </summary>
public sealed class WeeklyReport : IHostedService, IDisposable
{
    private static readonly object Lock = new();
    private static readonly string[] FailedOutcomes = { "none-found", "no-safe-match", "error", "failed", "whisper-failed" };
    private readonly Notifier _notifier;
    private readonly DownloadStateStore _state;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<WeeklyReport> _logger;
    private Timer? _timer;
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="WeeklyReport"/> class.
    /// </summary>
    /// <param name="notifier">Notifier.</param>
    /// <param name="state">Download state.</param>
    /// <param name="libraryManager">Library.</param>
    /// <param name="logger">Logger.</param>
    public WeeklyReport(Notifier notifier, DownloadStateStore state, ILibraryManager libraryManager, ILogger<WeeklyReport> logger)
    {
        _notifier = notifier;
        _state = state;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Gets or sets the storage folder (tests; default: the plugin's data folder).</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>
    /// Counts one created subtitle.
    /// </summary>
    /// <param name="kind">Origin ("download", "translation", "whisper", "ocr", "plugin").</param>
    public static void Count(string kind)
    {
        lock (Lock)
        {
            var data = Load();
            data.Counts[kind] = data.Counts.GetValueOrDefault(kind) + 1;
            Save(data);
        }
    }

    /// <summary>
    /// Whether the summary is due now.
    /// </summary>
    /// <param name="nowLocal">Local time.</param>
    /// <param name="lastSentUtc">Last summary.</param>
    /// <param name="day">Weekday (0 = Sunday).</param>
    /// <param name="hour">Hour.</param>
    /// <returns>True when due.</returns>
    internal static bool IsDue(DateTime nowLocal, DateTime? lastSentUtc, int day, int hour)
    {
        if ((int)nowLocal.DayOfWeek != Math.Clamp(day, 0, 6) || nowLocal.Hour < Math.Clamp(hour, 0, 23))
        {
            return false;
        }

        return lastSentUtc is null || nowLocal.ToUniversalTime() - lastSentUtc.Value > TimeSpan.FromDays(6);
    }

    /// <summary>
    /// Builds the text of the summary.
    /// </summary>
    /// <param name="counts">Created subtitles per kind.</param>
    /// <param name="failed">Titles without a fitting subtitle.</param>
    /// <returns>Title, short line and body.</returns>
    internal static (string Title, string Short, string Body) Compose(IReadOnlyDictionary<string, int> counts, IReadOnlyList<string> failed)
    {
        static string Name(string kind) => kind switch
        {
            "download" => Loc.T("downloaded", "heruntergeladen"),
            "translation" => Loc.T("translated", "übersetzt"),
            "whisper" => Loc.T("generated with Whisper", "mit Whisper erzeugt"),
            "ocr" => Loc.T("converted with OCR", "per OCR umgewandelt"),
            _ => Loc.T("other (sync fixes, conversions)", "sonstige (Sync-Korrekturen, Umwandlungen)"),
        };

        var total = counts.Values.Sum();
        var title = Loc.German
            ? FormattableString.Invariant($"SubtitleForge Wochenbericht: {total} neue Untertitel")
            : FormattableString.Invariant($"SubtitleForge weekly report: {total} new subtitles");
        var body = new StringBuilder();
        foreach (var kv in counts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value))
        {
            body.Append("• ").Append(kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ').AppendLine(Name(kv.Key));
        }

        if (failed.Count > 0)
        {
            body.AppendLine().AppendLine(Loc.German
                ? FormattableString.Invariant($"Nichts Passendes gefunden ({failed.Count}):")
                : FormattableString.Invariant($"Nothing fitting found ({failed.Count}):"));
            foreach (var f in failed.Take(15))
            {
                body.Append("• ").AppendLine(f);
            }

            if (failed.Count > 15)
            {
                body.AppendLine(Loc.German
                    ? FormattableString.Invariant($"… und {failed.Count - 15} weitere")
                    : FormattableString.Invariant($"… and {failed.Count - 15} more"));
            }
        }

        var shortLine = Loc.German
            ? FormattableString.Invariant($"{total} neu, {failed.Count} ohne passenden Untertitel")
            : FormattableString.Invariant($"{total} new, {failed.Count} without a fitting subtitle");
        return (title, shortLine, body.ToString());
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    private static string? FilePath()
    {
        var dir = FolderOverride ?? Plugin.Instance?.DataFolderPath;
        return dir is null ? null : Path.Combine(dir, "weekly-report.json");
    }

    private static Data Load()
    {
        try
        {
            if (FilePath() is { } path && File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new Data();
                data.Counts = new Dictionary<string, int>(data.Counts ?? new Dictionary<string, int>(), StringComparer.Ordinal);
                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Data();
    }

    private static void Save(Data data)
    {
        try
        {
            if (FilePath() is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data));
                File.Move(path + ".tmp", path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            SubtitleTrash.Purge();
            var config = Plugin.Instance?.Configuration;
            Data data;
            lock (Lock)
            {
                data = Load();
            }

            if (config is null || !config.WeeklyReport)
            {
                return;
            }

            if (!IsDue(DateTime.Now, data.LastSentUtc, config.WeeklyReportDay, config.WeeklyReportHour))
            {
                return;
            }

            var since = DateTime.UtcNow.AddDays(-7);
            var failed = _state.All()
                .Where(e => e.Entry.LastTriedUtc >= since && FailedOutcomes.Contains(e.Entry.Outcome) && !e.Key.StartsWith("block:", StringComparison.Ordinal) && !e.Key.StartsWith("ocr:", StringComparison.Ordinal))
                .Select(e => (e.ItemId, Lang: e.Key))
                .Distinct()
                .Select(e => _libraryManager.GetItemById(e.ItemId) is MediaBrowser.Controller.Entities.Video v ? SubtitleFileWriter.DisplayName(v) + " (" + e.Lang + ")" : null)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList();
            Dictionary<string, int> counts;
            lock (Lock)
            {
                data = Load();
                counts = data.Counts;
                data.Counts = new Dictionary<string, int>(StringComparer.Ordinal);
                data.LastSentUtc = DateTime.UtcNow;
                Save(data);
            }

            var (title, shortLine, body) = Compose(counts, failed);
            await _notifier.DeliverAsync(title, shortLine, body, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SubtitleForge: weekly report failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private sealed class Data
    {
        public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);

        public DateTime? LastSentUtc { get; set; }
    }
}
