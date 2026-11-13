using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Scheduled task: downloads missing subtitles in the wanted languages (verified against the audio),
/// with Whisper as last resort.
/// </summary>
public class DownloadTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ItemProcessor _processor;
    private readonly SubtitleSearchService _search;
    private readonly ILogger<DownloadTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="processor">Item processor.</param>
    /// <param name="search">Search service.</param>
    /// <param name="logger">Logger.</param>
    public DownloadTask(ILibraryManager libraryManager, ItemProcessor processor, SubtitleSearchService search, ILogger<DownloadTask> logger)
    {
        _libraryManager = libraryManager;
        _processor = processor;
        _search = search;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Fehlende Untertitel herunterladen";

    /// <inheritdoc />
    public string Key => "SubtitleForgeDownload";

    /// <inheritdoc />
    public string Description => "Lädt fehlende Untertitel (SubDL, OpenSubtitles), prüft sie gegen die Tonspur und erzeugt sie notfalls mit Whisper.";

    /// <inheritdoc />
    public string Category => "SubtitleForge";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_search.Sources.Any(s => s.IsConfigured))
        {
            _logger.LogWarning("SubtitleForge download: no source configured (SubDL / OpenSubtitles API key missing) – only Whisper will be used");
        }

        var items = LibraryItems.Videos(_libraryManager);
        var written = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            written += await _processor.ProcessAsync(items[i], ProcessSteps.Download | ProcessSteps.Whisper, false, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / Math.Max(1, items.Count));
        }

        _logger.LogInformation("SubtitleForge download: {Count} subtitle(s) written", written);
    }
}
