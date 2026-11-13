using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Saves generated subtitles next to the media (like Jellyfin's own subtitle download), falling back to the
/// item's internal metadata folder when the media folder is read-only, then queues an item refresh.
/// </summary>
public class SubtitleFileWriter
{
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<SubtitleFileWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFileWriter"/> class.
    /// </summary>
    /// <param name="libraryMonitor">Library monitor.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="logger">Logger.</param>
    public SubtitleFileWriter(ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem, ILogger<SubtitleFileWriter> logger)
    {
        _libraryMonitor = libraryMonitor;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Builds the file name stem (without folder and extension), e.g. "Movie.ger.forced.OCR".
    /// Jellyfin parses language, forced flag and the trailing token as track title.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">Language code.</param>
    /// <param name="forced">Forced flag.</param>
    /// <param name="tag">Title token (e.g. "OCR").</param>
    /// <returns>Stem.</returns>
    public static string BuildStem(Video video, string language, bool forced, string tag)
    {
        var stem = Path.GetFileNameWithoutExtension(video.Path) + "." + language;
        if (forced)
        {
            stem += ".forced";
        }

        return stem + "." + tag;
    }

    /// <summary>
    /// Gets the candidate paths (media folder first, then internal metadata folder).
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="stem">Stem.</param>
    /// <returns>Full paths with .srt extension.</returns>
    public static IReadOnlyList<string> CandidatePaths(Video video, string stem)
    {
        return new[]
        {
            Path.Combine(video.ContainingFolderPath, stem + ".srt"),
            Path.Combine(video.GetInternalMetadataPath(), stem + ".srt"),
        };
    }

    /// <summary>
    /// Whether a subtitle with this stem already exists in any candidate location.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="stem">Stem.</param>
    /// <returns>True if present.</returns>
    public static bool Exists(Video video, string stem)
    {
        foreach (var p in CandidatePaths(video, stem))
        {
            if (File.Exists(p))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the SRT and queues a refresh of the item.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="stem">Stem.</param>
    /// <param name="srtContent">SRT content.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Saved path.</returns>
    public async Task<string> SaveAsync(Video video, string stem, string srtContent, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(false).GetBytes(srtContent);
        List<Exception>? errors = null;
        foreach (var path in CandidatePaths(video, stem))
        {
            _libraryMonitor.ReportFileSystemChangeBeginning(path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                _logger.LogInformation("SubtitleForge: saved {Path}", path);
                _providerManager.QueueRefresh(video.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("SubtitleForge: cannot write {Path}: {Message}", path, ex.Message);
                (errors ??= new List<Exception>()).Add(ex);
            }
            finally
            {
                _libraryMonitor.ReportFileSystemChangeComplete(path, false);
            }
        }

        throw new AggregateException("Could not save subtitle in any location", errors!);
    }
}
