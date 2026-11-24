using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using Jellyfin.Plugin.SubtitleForge.Ocr;
using Jellyfin.Plugin.SubtitleForge.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Sync;

/// <summary>
/// Scheduled task: checks existing external SRT subtitles against the audio and corrects their timing.
/// </summary>
public class SyncTask : IScheduledTask
{
    private const long MinCorrectionMs = 150;


    private readonly ILibraryManager _libraryManager;
    private readonly SyncService _sync;
    private readonly Throttle _throttle;
    private readonly Notifier _notifier;
    private readonly DownloadStateStore _state;
    private readonly ILogger<SyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sync">Sync service.</param>
    /// <param name="state">State store.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="throttle">Throttle.</param>
    /// <param name="notifier">Notifier.</param>
    public SyncTask(ILibraryManager libraryManager, SyncService sync, DownloadStateStore state, ILogger<SyncTask> logger, Throttle throttle, Notifier notifier)
    {
        _throttle = throttle;
        _notifier = notifier;
        _libraryManager = libraryManager;
        _sync = sync;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Loc.T("Check and fix subtitle sync", "Untertitel-Synchronität prüfen und korrigieren");

    /// <inheritdoc />
    public string Key => "SubtitleForgeSync";

    /// <inheritdoc />
    public string Description => Loc.T("Matches existing external SRT subtitles against the audio track and fixes offset, frame rate and cut differences.", "Gleicht vorhandene externe SRT-Untertitel mit der Tonspur ab und korrigiert Versatz, Framerate und Schnittunterschiede.");

    /// <inheritdoc />
    public string Category => "SubtitleForge";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var items = LibraryItems.AllVideos(_libraryManager);

        var work = new List<(Video Video, string Path, bool Own)>();
        foreach (var video in items)
        {
            foreach (var s in video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path)))
            {
                if (!s.Path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || !File.Exists(s.Path))
                {
                    continue;
                }

                // OCR (OCR, OCR1 …), converted ASS ("Text"), translations and Whisper output take their timing from the
                // file itself: nothing to correct.
                var origin = SubtitleOrigin.Classify(video.Path, s.Path);
                if (origin.Origin is "ocr" or "whisper" or "translation" || (origin.Origin == "plugin" && origin.Tag.EndsWith("Text", StringComparison.Ordinal)))
                {
                    continue;
                }

                var own = origin.Origin is "download" or "plugin";
                if (!own && !config.SyncFixForeignSubtitles)
                {
                    continue;
                }

                var key = StateKey(s.Path);
                var last = _state.Get(video.Id, key);
                if (last is not null && last.Outcome is "ok" or "fixed" or "unsure" or "empty" && last.LastTriedUtc >= File.GetLastWriteTimeUtc(s.Path))
                {
                    continue;
                }

                work.Add((video, s.Path, own));
            }
        }

        _logger.LogInformation("SubtitleForge sync: {Count} subtitle file(s) to check", work.Count);
        int fixedCount = 0, okCount = 0, unsure = 0;
        for (var i = 0; i < work.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / Math.Max(1, work.Count));
            var (video, path, own) = work[i];
            await _throttle.WaitForPlaybackAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var outcome = await CheckAsync(video, path, own, cancellationToken).ConfigureAwait(false);
                _state.Set(video.Id, StateKey(path), outcome, null);
                switch (outcome)
                {
                    case "fixed": fixedCount++; break;
                    case "ok": okCount++; break;
                    default: unsure++; break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleForge sync failed for {Path}", path);
            }
        }

        _logger.LogInformation("SubtitleForge sync: {Fixed} corrected, {Ok} already in sync, {Unsure} not verifiable", fixedCount, okCount, unsure);
        if (fixedCount + unsure > 0)
        {
            await _notifier.FlushAsync("Sync", Loc.German
                    ? FormattableString.Invariant($"{fixedCount} korrigiert, {okCount} passten schon, {unsure} nicht prüfbar")
                    : FormattableString.Invariant($"{fixedCount} fixed, {okCount} already in sync, {unsure} could not be checked"), cancellationToken).ConfigureAwait(false);
        }
        progress.Report(100);
    }

    // Stable across restarts (string.GetHashCode is randomised per process).
    private static string StateKey(string path) =>
        "sync:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path)), 0, 8).ToLowerInvariant();

    private async Task<string> CheckAsync(Video video, string path, bool own, CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        if (before.LinkTarget is not null)
        {
            return "empty";
        }

        var stamp = (before.LastWriteTimeUtc, before.Length);
        var cues = SubtitleContent.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), Path.GetFileName(path), null, null);
        if (cues is null || cues.Count < 3)
        {
            return "empty";
        }

        var alignment = await _sync.AlignAsync(video, cues, false, cancellationToken).ConfigureAwait(false);
        if (alignment is null)
        {
            return "no-audio";
        }

        if (!alignment.IsConfident)
        {
            _logger.LogWarning("SubtitleForge sync: {Path} could not be verified – maybe the wrong subtitle for this file ({Alignment})", path, alignment);
            return "unsure";
        }

        if (alignment.MaxShiftMs < MinCorrectionMs)
        {
            return "ok";
        }

        if (!own)
        {
            var backup = path + ".orig";
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
            }
        }

        // The analysis can take minutes: if the file was deleted, replaced or rejected meanwhile, leave it alone.
        var now = new FileInfo(path);
        if (!now.Exists || (now.LastWriteTimeUtc, now.Length) != stamp)
        {
            return "unsure";
        }

        await SubtitleFileWriter.WriteAtomicAsync(path, new UTF8Encoding(false).GetBytes(Srt.Write(alignment.Cues)), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("SubtitleForge sync: corrected {Path} ({Alignment})", path, alignment);
        return "fixed";
    }
}
