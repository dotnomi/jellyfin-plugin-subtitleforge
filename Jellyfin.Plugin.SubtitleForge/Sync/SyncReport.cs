using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Sync;

/// <summary>
/// One checked subtitle file.
/// </summary>
public sealed class SyncReportRow
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle file name.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets ok, shifted, unsure, no-audio or empty.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Gets or sets the largest correction the aligner would apply (ms).</summary>
    public long MaxShiftMs { get; set; }

    /// <summary>Gets or sets the global offset (ms).</summary>
    public long OffsetMs { get; set; }

    /// <summary>Gets or sets the framerate ratio applied.</summary>
    public double Ratio { get; set; } = 1;

    /// <summary>Gets or sets the number of segments with different shifts.</summary>
    public int Segments { get; set; } = 1;

    /// <summary>Gets or sets the share of the subtitle overlapping speech before → after correction.</summary>
    public string Overlap { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a corrected copy was written.</summary>
    public bool Fixed { get; set; }
}

/// <summary>
/// Checks all external SRT files (including ones SubtitleForge did not make) against the audio and reports which are
/// out of sync – without changing anything. Corrections are applied per file from the overview.
/// </summary>
public class SyncReportTask : IScheduledTask
{
    private const long ShiftedMs = 150;
    private readonly ILibraryManager _libraryManager;
    private readonly SyncService _sync;
    private readonly Throttle _throttle;
    private readonly Notifier _notifier;
    private readonly ILogger<SyncReportTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncReportTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sync">Sync service.</param>
    /// <param name="throttle">Throttle.</param>
    /// <param name="notifier">Notifier.</param>
    /// <param name="logger">Logger.</param>
    public SyncReportTask(ILibraryManager libraryManager, SyncService sync, Throttle throttle, Notifier notifier, ILogger<SyncReportTask> logger)
    {
        _libraryManager = libraryManager;
        _sync = sync;
        _throttle = throttle;
        _notifier = notifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync-Bericht erstellen (nur prüfen)";

    /// <inheritdoc />
    public string Key => "SubtitleForgeSyncReport";

    /// <inheritdoc />
    public string Description => "Prüft alle externen SRT-Untertitel gegen die Tonspur und listet verschobene in der SubtitleForge-Übersicht auf. Ändert nichts.";

    /// <inheritdoc />
    public string Category => "SubtitleForge";

    /// <summary>Gets the report file path.</summary>
    public static string? ReportPath => Plugin.Instance is { } p ? Path.Combine(p.DataFolderPath, "sync-report.json") : null;

    /// <summary>
    /// Loads the last report.
    /// </summary>
    /// <returns>Rows (empty when none exists).</returns>
    public static List<SyncReportRow> Load()
    {
        try
        {
            return ReportPath is { } path && System.IO.File.Exists(path)
                ? JsonSerializer.Deserialize<List<SyncReportRow>>(System.IO.File.ReadAllText(path)) ?? new List<SyncReportRow>()
                : new List<SyncReportRow>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new List<SyncReportRow>();
        }
    }

    /// <summary>
    /// Saves the report.
    /// </summary>
    /// <param name="rows">Rows.</param>
    public static void Save(List<SyncReportRow> rows)
    {
        if (ReportPath is not { } path)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(rows));
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var work = new List<(Video Video, string Path)>();
        foreach (var video in LibraryItems.Videos(_libraryManager))
        {
            foreach (var s in video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !s.IsForced && !string.IsNullOrEmpty(s.Path)))
            {
                if (s.Path!.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(s.Path))
                {
                    work.Add((video, s.Path));
                }
            }
        }

        var rows = new List<SyncReportRow>();
        for (var i = 0; i < work.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / Math.Max(1, work.Count));
            await _throttle.WaitForPlaybackAsync(cancellationToken).ConfigureAwait(false);
            var (video, path) = work[i];
            var row = new SyncReportRow { ItemId = video.Id, Name = SubtitleFileWriter.DisplayName(video), File = Path.GetFileName(path) };
            try
            {
                var cues = SubtitleContent.Parse(await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), row.File, null, null);
                if (cues is null || cues.Count < 3)
                {
                    row.Outcome = "empty";
                }
                else
                {
                    var a = await _sync.AlignAsync(video, cues, false, cancellationToken).ConfigureAwait(false);
                    row.Outcome = a is null ? "no-audio" : !a.IsConfident ? "unsure" : a.MaxShiftMs >= ShiftedMs ? "shifted" : "ok";
                    if (a is not null)
                    {
                        row.MaxShiftMs = a.MaxShiftMs;
                        row.OffsetMs = a.OffsetMs;
                        row.Ratio = a.Ratio;
                        row.Segments = a.Segments;
                        row.Overlap = FormattableString.Invariant($"{a.SpeechOverlapBefore:P0} → {a.SpeechOverlap:P0}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SubtitleForge sync report: {Path}", path);
                row.Outcome = "error";
            }

            rows.Add(row);
        }

        Save(rows);
        var shifted = rows.Count(r => r.Outcome == "shifted");
        var unsure = rows.Count(r => r.Outcome == "unsure");
        _logger.LogInformation("SubtitleForge sync report: {Count} files, {Shifted} shifted, {Unsure} unsure", rows.Count, shifted, unsure);
        await _notifier.FlushAsync("Sync-Bericht", $"{rows.Count} Dateien geprüft: {shifted} verschoben, {unsure} passen evtl. nicht zur Datei", cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
