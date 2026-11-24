using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Once after updating from a version without the "[SF]" mark: renames the subtitle files SubtitleForge made, so
/// every file of the plugin carries the mark (e.g. "Movie.de.SubDL.srt" → "Movie.de.[SF] SubDL.srt").
/// Files from elsewhere are never touched.
/// </summary>
public sealed class BrandMigration : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleFileWriter _writer;
    private readonly ILogger<BrandMigration> _logger;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BrandMigration"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="writer">Writer.</param>
    /// <param name="logger">Logger.</param>
    public BrandMigration(ILibraryManager libraryManager, SubtitleFileWriter writer, ILogger<BrandMigration> logger)
    {
        _libraryManager = libraryManager;
        _writer = writer;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), _stop.Token).ConfigureAwait(false);
                    if (Plugin.Instance is { Configuration.BrandMigrationDone: false } plugin)
                    {
                        Run(_stop.Token);
                        plugin.Configuration.BrandMigrationDone = true;
                        plugin.SaveConfiguration();
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SubtitleForge: renaming older subtitle files failed");
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _stop.CancelAsync();

    /// <inheritdoc />
    public void Dispose() => _stop.Dispose();

    /// <summary>
    /// The new name for an own file without the mark, or null if it needs none.
    /// </summary>
    /// <param name="videoPath">Video path.</param>
    /// <param name="path">Subtitle path.</param>
    /// <returns>New path or null.</returns>
    public static string? BrandedPath(string videoPath, string path)
    {
        var origin = SubtitleOrigin.Classify(videoPath, path);
        if (!origin.IsOwn || origin.Branded || origin.Language is null || !path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var forced = origin.Tag.StartsWith("forced.", StringComparison.Ordinal);
        var bare = forced ? origin.Tag[7..] : origin.Tag;
        var name = Path.GetFileNameWithoutExtension(videoPath) + "." + origin.Language + (forced ? ".forced." : ".") + SubtitleFileWriter.Brand + bare + ".srt";
        return Path.Combine(Path.GetDirectoryName(path)!, name);
    }

    private void Run(CancellationToken cancellationToken)
    {
        var renamed = 0;
        foreach (var video in LibraryItems.AllVideos(_libraryManager))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var any = false;
            foreach (var path in video.GetMediaStreams()
                .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path))
                .Select(s => s.Path!)
                .Distinct())
            {
                if (BrandedPath(video.Path, path) is { } target && File.Exists(path) && _writer.Rename(path, target))
                {
                    renamed++;
                    any = true;
                }
            }

            if (any)
            {
                _writer.Refresh(video);
            }
        }

        _logger.LogInformation("SubtitleForge: added the [SF] mark to {Count} older subtitle files", renamed);
    }
}
