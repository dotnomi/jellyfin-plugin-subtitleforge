using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly Notifier _notifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleFileWriter"/> class.
    /// </summary>
    /// <param name="libraryMonitor">Library monitor.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="notifier">Notifier.</param>
    public SubtitleFileWriter(ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem, ILogger<SubtitleFileWriter> logger, Notifier notifier)
    {
        _notifier = notifier;
        _libraryMonitor = libraryMonitor;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>Marks every subtitle file SubtitleForge writes; Jellyfin shows it in the track title.</summary>
    public const string Brand = "[SF] ";

    /// <summary>
    /// Removes the SubtitleForge mark from a file name tag ("[SF] SubDL" → "SubDL"); tags without it stay as they are.
    /// </summary>
    /// <param name="tag">Tag.</param>
    /// <returns>Tag without mark.</returns>
    public static string StripBrand(string tag) => tag.StartsWith(Brand, StringComparison.Ordinal) ? tag[Brand.Length..] : tag;

    /// <summary>
    /// The last name token of a subtitle stem (after the last '.') without the SubtitleForge mark.
    /// </summary>
    /// <param name="stem">Stem.</param>
    /// <returns>Tag.</returns>
    public static string TagOf(string stem) => StripBrand(stem[(stem.LastIndexOf('.') + 1)..]);

    /// <summary>
    /// The stem as older versions (without "[SF] ") named it.
    /// </summary>
    /// <param name="stem">Stem.</param>
    /// <returns>Legacy stem, or the same stem.</returns>
    public static string LegacyStem(string stem)
    {
        var i = stem.LastIndexOf("." + Brand, StringComparison.Ordinal);
        return i < 0 ? stem : stem[..(i + 1)] + stem[(i + 1 + Brand.Length)..];
    }

    /// <summary>
    /// Builds the file name stem (without folder and extension), e.g. "Movie.ger.forced.[SF] OCR".
    /// Jellyfin parses language, forced flag and the trailing token as track title.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">Language code.</param>
    /// <param name="forced">Forced flag.</param>
    /// <param name="tag">Title token (e.g. "OCR").</param>
    /// <returns>Stem.</returns>
    public static string BuildStem(Video video, string language, bool forced, string tag)
    {
        // Language and tag become part of a file name: never allow path separators or "..".
        if (!IsSafeNamePart(language) || !IsSafeNamePart(tag))
        {
            throw new ArgumentException("Unsafe subtitle file name part");
        }

        var stem = Path.GetFileNameWithoutExtension(video.Path) + "." + language;
        if (forced)
        {
            stem += ".forced";
        }

        return stem + "." + Brand + StripBrand(tag);
    }

    private static bool IsSafeNamePart(string part) =>
        part.Length > 0 && part.Length < 100 && !part.Contains("..", StringComparison.Ordinal)
        && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && part.AsSpan().IndexOfAny(UnsafeNameChars) < 0;

    private static readonly System.Buffers.SearchValues<char> UnsafeNameChars = System.Buffers.SearchValues.Create("/\\:");

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
        foreach (var p in CandidatePaths(video, stem).Concat(CandidatePaths(video, LegacyStem(stem))))
        {
            if (File.Exists(p))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes subtitles that SubtitleForge downloaded itself (file name tag = source name) for a language.
    /// Files from other origins are never touched.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">Language code used in file names.</param>
    /// <param name="sourceTags">Source tags (e.g. SubDL, OpenSubtitles).</param>
    /// <returns>Number of deleted files.</returns>
    public int DeleteOwnDownloads(Video video, string language, IEnumerable<string> sourceTags)
    {
        var deleted = 0;
        foreach (var tag in sourceTags)
        {
            var stem = BuildStem(video, language, false, tag);
            foreach (var path in CandidatePaths(video, stem).Concat(CandidatePaths(video, LegacyStem(stem))).Distinct())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                _libraryMonitor.ReportFileSystemChangeBeginning(path);
                try
                {
                    File.Delete(path);
                    deleted++;
                    _logger.LogInformation("SubtitleForge: removed {Path}", path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("SubtitleForge: cannot delete {Path}: {Message}", path, ex.Message);
                }
                finally
                {
                    _libraryMonitor.ReportFileSystemChangeComplete(path, false);
                }
            }
        }

        if (deleted > 0)
        {
            _providerManager.QueueRefresh(video.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes SubtitleForge's own subtitle files of a language whose tag matches, except one path to keep.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="language">Language code used in file names.</param>
    /// <param name="tagMatches">Predicate on the file name tag (token before ".srt").</param>
    /// <param name="keepPath">File to keep.</param>
    /// <returns>Number of deleted files.</returns>
    public int DeleteOwn(Video video, string language, Func<string, bool> tagMatches, string? keepPath)
    {
        var prefix = Path.GetFileNameWithoutExtension(video.Path) + "." + language + ".";
        var deleted = 0;
        foreach (var dir in new[] { video.ContainingFolderPath, video.GetInternalMetadataPath() }.Distinct())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(dir, "*.srt"))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || string.Equals(path, keepPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var tag = Path.GetFileNameWithoutExtension(name)[prefix.Length..];
                if (tag.StartsWith("forced.", StringComparison.Ordinal) || !tagMatches(StripBrand(tag)))
                {
                    continue;
                }

                _libraryMonitor.ReportFileSystemChangeBeginning(path);
                try
                {
                    File.Delete(path);
                    deleted++;
                    _logger.LogInformation("SubtitleForge: removed {Path}", path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("SubtitleForge: cannot delete {Path}: {Message}", path, ex.Message);
                }
                finally
                {
                    _libraryMonitor.ReportFileSystemChangeComplete(path, false);
                }
            }
        }

        if (deleted > 0)
        {
            _providerManager.QueueRefresh(video.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes one subtitle file of a video (the caller checks that the plugin made it) and refreshes the item.
    /// </summary>
    /// <param name="video">Video.</param>
    /// <param name="path">Full path of the subtitle file.</param>
    /// <returns>Whether it was deleted.</returns>
    public bool DeleteFile(Video video, string path)
    {
        _libraryMonitor.ReportFileSystemChangeBeginning(path);
        try
        {
            File.Delete(path);
            _logger.LogInformation("SubtitleForge: removed {Path} (by user)", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("SubtitleForge: cannot delete {Path}: {Message}", path, ex.Message);
            return false;
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(path, false);
        }

        _providerManager.QueueRefresh(video.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
        return true;
    }

    /// <summary>
    /// Renames a subtitle file (used to add the SubtitleForge mark to files of older versions).
    /// </summary>
    /// <param name="from">Current path.</param>
    /// <param name="to">New path.</param>
    /// <returns>Whether it was renamed.</returns>
    public bool Rename(string from, string to)
    {
        if (File.Exists(to))
        {
            return false;
        }

        _libraryMonitor.ReportFileSystemChangeBeginning(from);
        _libraryMonitor.ReportFileSystemChangeBeginning(to);
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("SubtitleForge: cannot rename {Path}: {Message}", from, ex.Message);
            return false;
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(from, false);
            _libraryMonitor.ReportFileSystemChangeComplete(to, false);
        }
    }

    /// <summary>
    /// Queues a metadata refresh so Jellyfin picks up changed subtitle files.
    /// </summary>
    /// <param name="video">Video.</param>
    public void Refresh(Video video) =>
        _providerManager.QueueRefresh(video.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Low);

    /// <summary>
    /// Writes a file atomically: a new, randomly named temp file (never follows a planted link, fails if it exists),
    /// then renamed over the target (replaces a link at the target instead of writing through it).
    /// </summary>
    /// <param name="path">Target path.</param>
    /// <param name="bytes">Content.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Task.</returns>
    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".sf-tmp");
        try
        {
            var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            // Keep the permissions of a file that is replaced (shared media folders).
            if (!OperatingSystem.IsWindows() && File.Exists(path))
            {
                try
                {
                    File.SetUnixFileMode(tmp, File.GetUnixFileMode(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private void RemoveLegacyCopy(string legacy, string current)
    {
        if (string.Equals(legacy, current, StringComparison.Ordinal) || !File.Exists(legacy))
        {
            return;
        }

        try
        {
            File.Delete(legacy);
            _logger.LogInformation("SubtitleForge: removed older copy {Path}", legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("SubtitleForge: cannot delete {Path}: {Message}", legacy, ex.Message);
        }
    }

    /// <summary>
    /// Short display name ("Series S01E02" or "Movie (2020)").
    /// </summary>
    /// <param name="video">Video.</param>
    /// <returns>Name.</returns>
    public static string DisplayName(Video video) => video is MediaBrowser.Controller.Entities.TV.Episode e
        ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00}"
        : video.ProductionYear is { } y && !video.Name.Contains($"({y})", System.StringComparison.Ordinal) ? $"{video.Name} ({y})" : video.Name;

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
                await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("SubtitleForge: saved {Path}", path);
                RemoveLegacyCopy(Path.Combine(Path.GetDirectoryName(path)!, LegacyStem(stem) + ".srt"), path);
                _notifier.Record(DisplayName(video) + ": " + stem[(Path.GetFileNameWithoutExtension(video.Path).Length + 1)..]);
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
