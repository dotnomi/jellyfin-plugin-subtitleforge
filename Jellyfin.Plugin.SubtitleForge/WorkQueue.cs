using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge;

/// <summary>
/// Kind of queued work.
/// </summary>
public enum WorkKind
{
    /// <summary>Full pipeline for a newly added item.</summary>
    NewItem,

    /// <summary>Search/download again now (manual).</summary>
    Search,

    /// <summary>Generate with Whisper (manual).</summary>
    Whisper,
}

/// <summary>
/// A queued job.
/// </summary>
/// <param name="ItemId">Item.</param>
/// <param name="Kind">Kind.</param>
/// <param name="Language">Target language (Whisper).</param>
/// <param name="AudioIndex">Audio stream (Whisper).</param>
/// <param name="DueUtc">Earliest start.</param>
/// <param name="ReplaceDownload">Whisper: replace SubtitleForge's own downloaded subtitle of that language.</param>
public sealed record WorkItem(Guid ItemId, WorkKind Kind, string? Language, int? AudioIndex, DateTime DueUtc, bool ReplaceDownload = false);

/// <summary>
/// Background queue: processes newly added items a few minutes after the library scan, and runs manual actions.
/// </summary>
public sealed class WorkQueue : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ItemProcessor _processor;
    private readonly ILogger<WorkQueue> _logger;
    private readonly ConcurrentDictionary<string, WorkItem> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private DateTime _lastNewItemUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkQueue"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="processor">Processor.</param>
    /// <param name="logger">Logger.</param>
    public WorkQueue(ILibraryManager libraryManager, ItemProcessor processor, ILogger<WorkQueue> logger)
    {
        _libraryManager = libraryManager;
        _processor = processor;
        _logger = logger;
    }

    /// <summary>Gets the job currently running, if any.</summary>
    public WorkItem? Current { get; private set; }

    /// <summary>Gets the last results of manual actions (newest first).</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>
    /// Gets the pending jobs.
    /// </summary>
    /// <returns>Jobs ordered by due time.</returns>
    public IReadOnlyList<WorkItem> Pending() => _pending.Values.OrderBy(w => w.DueUtc).ToList();

    /// <summary>
    /// Adds or replaces a job (same item + kind + language = one job; re-adding postpones it).
    /// </summary>
    /// <param name="item">Job.</param>
    public void Enqueue(WorkItem item) => _pending[Key(item)] = item;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stop.Dispose();

    private static TimeSpan NewItemDelay() =>
        TimeSpan.FromMinutes(Math.Max(1, (Plugin.Instance?.Configuration ?? new PluginConfiguration()).NewItemDelayMinutes));

    private static string Key(WorkItem w) => $"{w.ItemId:N}:{w.Kind}:{w.Language}";

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        if (!config.ProcessNewItems || e.Item is not (Movie or Episode) || e.Item.IsVirtualItem
            || !LibraryItems.IsEnabled(_libraryManager, e.Item))
        {
            return;
        }

        _lastNewItemUtc = DateTime.UtcNow;
        Enqueue(new WorkItem(e.Item.Id, WorkKind.NewItem, null, null, DateTime.UtcNow.Add(NewItemDelay())));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                // New items only start once no further item was added for the configured delay (library scan finished).
                var quiet = DateTime.UtcNow - _lastNewItemUtc >= NewItemDelay();
                var due = _pending.Values
                    .Where(w => w.DueUtc <= DateTime.UtcNow && (w.Kind != WorkKind.NewItem || quiet))
                    .OrderBy(w => w.Kind == WorkKind.NewItem ? 1 : 0)
                    .ThenBy(w => w.DueUtc)
                    .FirstOrDefault();
                if (due is null || !_pending.TryRemove(Key(due), out _))
                {
                    continue;
                }

                Current = due;
                await RunAsync(due, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleForge queue job failed");
            }
            finally
            {
                Current = null;
            }
        }
    }

    private async Task RunAsync(WorkItem job, CancellationToken token)
    {
        if (_libraryManager.GetItemById(job.ItemId) is not Video video)
        {
            return;
        }

        string message;
        switch (job.Kind)
        {
            case WorkKind.NewItem:
                var n = await _processor.ProcessAsync(video, ProcessSteps.All, false, token).ConfigureAwait(false);
                message = $"{video.Name}: {n} Untertitel erstellt";
                break;
            case WorkKind.Search:
                var s = await _processor.ProcessAsync(video, ProcessSteps.Ocr | ProcessSteps.Download, true, token).ConfigureAwait(false);
                message = $"{video.Name}: Suche abgeschlossen, {s} Untertitel gespeichert";
                break;
            case WorkKind.Whisper:
                var outcome = await _processor.WhisperAsync(video, job.Language ?? "de", job.AudioIndex, job.ReplaceDownload, token).ConfigureAwait(false);
                message = $"{video.Name}: {outcome.Message}";
                break;
            default:
                return;
        }

        _logger.LogInformation("SubtitleForge queue: {Message}", message);
        Log.Enqueue($"{DateTime.Now:HH:mm} {message}");
        while (Log.Count > 20)
        {
            Log.TryDequeue(out _);
        }
    }
}
