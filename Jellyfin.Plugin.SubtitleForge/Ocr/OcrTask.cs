using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitleForge.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Scheduled task: converts embedded image subtitles (PGS) into text subtitles once per file.
/// </summary>
public class OcrTask : IScheduledTask
{
    /// <summary>Tag used in generated file names; shows up as track title in Jellyfin.</summary>
    public const string FileTag = "OCR";

    private readonly ILibraryManager _libraryManager;
    private readonly ItemProcessor _processor;
    private readonly ILogger<OcrTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OcrTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="processor">Item processor.</param>
    /// <param name="logger">Logger.</param>
    public OcrTask(ILibraryManager libraryManager, ItemProcessor processor, ILogger<OcrTask> logger)
    {
        _libraryManager = libraryManager;
        _processor = processor;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Bild-Untertitel in Text umwandeln (OCR)";

    /// <inheritdoc />
    public string Key => "SubtitleForgeOcr";

    /// <inheritdoc />
    public string Description => "Wandelt eingebettete PGS-Bilduntertitel in den gewünschten Sprachen einmalig per OCR in SRT um.";

    /// <inheritdoc />
    public string Category => "SubtitleForge";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(3).Ticks },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = LibraryItems.Videos(_libraryManager);
        var written = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            written += await _processor.ProcessAsync(items[i], ProcessSteps.Ocr, false, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / Math.Max(1, items.Count));
        }

        _logger.LogInformation("SubtitleForge OCR: {Count} subtitle(s) written", written);
    }
}
