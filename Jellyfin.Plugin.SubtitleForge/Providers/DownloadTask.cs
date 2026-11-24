using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
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
    private readonly Notifier _notifier;
    private readonly Throttle _throttle;
    private readonly LanguageResolver _languages;
    private readonly DownloadStateStore _state;
    private readonly SubtitleSearchService _search;
    private readonly ILogger<DownloadTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="processor">Item processor.</param>
    /// <param name="search">Search service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="notifier">Notifier.</param>
    /// <param name="throttle">Throttle.</param>
    /// <param name="languages">Language resolver.</param>
    /// <param name="state">State store.</param>
    public DownloadTask(ILibraryManager libraryManager, ItemProcessor processor, SubtitleSearchService search, ILogger<DownloadTask> logger, Notifier notifier, Throttle throttle, LanguageResolver languages, DownloadStateStore state)
    {
        _throttle = throttle;
        _languages = languages;
        _state = state;
        _notifier = notifier;
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
        var lastResort = await WhisperLastResortAsync(items, cancellationToken).ConfigureAwait(false);
        await _notifier.FlushAsync("Download", lastResort > 0 ? $"{lastResort} Folge(n) per Whisper ergänzt (kein Download seit Tagen)" : null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks items whose subtitle has been missing for a long time (no download found).
    /// </summary>
    /// <param name="items">Videos.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="hasLanguage">Whether the video already has a subtitle in that language.</param>
    /// <param name="state">State lookup.</param>
    /// <param name="wanted">Wanted languages per video.</param>
    /// <param name="nowUtc">Now.</param>
    /// <returns>(video, language) pairs, oldest gap first, at most the configured number.</returns>
    internal static List<(Video Video, string Language)> PickLastResort(
        IEnumerable<Video> items,
        PluginConfiguration config,
        Func<Video, string, bool> hasLanguage,
        Func<Video, string, DownloadStateStore.Entry?> state,
        Func<Video, IReadOnlyList<string>> wanted,
        DateTime nowUtc)
    {
        var limit = nowUtc.AddDays(-Math.Max(0, config.WhisperLastResortAfterDays));
        return items
            .SelectMany(v => wanted(v).Select(l => (Video: v, Language: l)))
            .Where(x => !hasLanguage(x.Video, x.Language))
            .Select(x => (x.Video, x.Language, Entry: state(x.Video, x.Language)))
            .Where(x => x.Entry is { Outcome: not "saved", FirstMissingUtc: { } first } && first <= limit
                && state(x.Video, "whisper:" + x.Language) is null)
            .OrderBy(x => x.Entry!.FirstMissingUtc)
            .Take(Math.Max(0, config.WhisperLastResortPerRun))
            .Select(x => (x.Video, x.Language))
            .ToList();
    }

    private async Task<int> WhisperLastResortAsync(List<Video> items, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.WhisperEnabled || !config.WhisperLastResort)
        {
            return 0;
        }

        var picks = PickLastResort(
            items,
            config,
            (v, l) => v.GetMediaStreams().Any(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && Lang.Same(s.Language, l)),
            (v, l) => _state.Get(v.Id, l),
            v => _languages.Resolve(v, out _),
            DateTime.UtcNow);
        var done = 0;
        foreach (var (video, lang) in picks)
        {
            await _throttle.WaitForAutomaticWorkAsync(cancellationToken).ConfigureAwait(false);
            var outcome = await _processor.WhisperAsync(video, lang, null, false, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("SubtitleForge Whisper (letzter Ausweg) {Name} {Lang}: {Message}", video.Name, lang, outcome.Message);
            done += outcome.Success ? 1 : 0;
        }

        return done;
    }
}
