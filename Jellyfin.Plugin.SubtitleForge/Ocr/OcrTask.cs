using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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

    private static readonly HashSet<string> PgsCodecs = new(StringComparer.OrdinalIgnoreCase) { "pgssub", "hdmv_pgs_subtitle", "pgs", "sup" };
    private static readonly HashSet<string> ImageCodecs = new(StringComparer.OrdinalIgnoreCase) { "pgssub", "hdmv_pgs_subtitle", "pgs", "sup", "dvdsub", "dvd_subtitle", "vobsub", "dvbsub", "dvb_subtitle", "xsub" };

    private readonly ILibraryManager _libraryManager;
    private readonly FfmpegRunner _ffmpeg;
    private readonly SubtitleFileWriter _writer;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<OcrTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OcrTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="ffmpeg">ffmpeg runner.</param>
    /// <param name="writer">Subtitle writer.</param>
    /// <param name="appPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public OcrTask(ILibraryManager libraryManager, FfmpegRunner ffmpeg, SubtitleFileWriter writer, IApplicationPaths appPaths, ILogger<OcrTask> logger)
    {
        _libraryManager = libraryManager;
        _ffmpeg = ffmpeg;
        _writer = writer;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Bild-Untertitel in Text umwandeln (OCR)";

    /// <inheritdoc />
    public string Key => "SubtitleForgeOcr";

    /// <inheritdoc />
    public string Description => "Wandelt eingebettete PGS-Bilduntertitel einmalig per OCR in SRT-Textuntertitel um.";

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
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var languages = new HashSet<string>(
            config.OcrLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        var jobs = FindJobs(config, languages);
        _logger.LogInformation("SubtitleForge OCR: {Count} image subtitle stream(s) to convert", jobs.Count);
        if (jobs.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var tempDir = Path.Combine(_appPaths.TempDirectory, "subtitleforge");
        Directory.CreateDirectory(tempDir);
        using var recognizer = new PaddleRecognizer(config.OcrThreads);

        for (var i = 0; i < jobs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[i];
            var baseProgress = 100.0 * i / jobs.Count;
            var sup = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".sup");
            try
            {
                var sw = Stopwatch.StartNew();
                await _ffmpeg.ExtractStreamAsync(job.Video.Path, job.Map, "sup", sup, cancellationToken).ConfigureAwait(false);

                OcrResult result;
                await using (var fs = File.OpenRead(sup))
                {
                    var itemProgress = new Progress<double>(p => progress.Report(baseProgress + (p * 100.0 / jobs.Count)));
                    result = ImageSubtitleOcr.Convert(fs, recognizer, itemProgress, cancellationToken);
                }

                if (result.Cues.Count == 0)
                {
                    _logger.LogWarning("SubtitleForge OCR: no text recognized in {Path} stream {Index}", job.Video.Path, job.Stream.Index);
                    continue;
                }

                var path = await _writer.SaveAsync(job.Video, job.Stem, Srt.Write(result.Cues), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "SubtitleForge OCR: {Path} – {Cues} cues, mean confidence {Conf:P1}, {Low} low-confidence lines, {Ms} ms",
                    path,
                    result.Cues.Count,
                    result.MeanConfidence,
                    result.LowConfidenceLines,
                    sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubtitleForge OCR failed for {Path} stream {Index}", job.Video.Path, job.Stream.Index);
            }
            finally
            {
                TryDelete(sup);
            }

            progress.Report(100.0 * (i + 1) / jobs.Count);
        }
    }

    private List<OcrJob> FindJobs(PluginConfiguration config, HashSet<string> languages)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo },
            IsVirtualItem = false,
            Recursive = true,
        });

        var jobs = new List<OcrJob>();
        foreach (var item in items.OfType<Video>())
        {
            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                continue;
            }

            var streams = item.GetMediaStreams();
            foreach (var s in streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && PgsCodecs.Contains(s.Codec ?? string.Empty)))
            {
                var lang = string.IsNullOrWhiteSpace(s.Language) ? "und" : s.Language;
                if (lang == "und" ? !config.OcrIncludeUndefined : !languages.Contains(lang))
                {
                    continue;
                }

                if (config.OcrSkipIfTextExists && streams.Any(o =>
                        o.Type == MediaStreamType.Subtitle
                        && !o.IsExternal
                        && !ImageCodecs.Contains(o.Codec ?? string.Empty)
                        && string.Equals(o.Language, s.Language, StringComparison.OrdinalIgnoreCase)
                        && o.IsForced == s.IsForced))
                {
                    continue;
                }

                // Several PGS streams with same language/forced flag (e.g. commentary) get an index suffix.
                var tag = FileTag;
                var sameKind = streams.Where(o => o.Type == MediaStreamType.Subtitle && !o.IsExternal
                    && PgsCodecs.Contains(o.Codec ?? string.Empty)
                    && string.Equals(o.Language, s.Language, StringComparison.OrdinalIgnoreCase)
                    && o.IsForced == s.IsForced).ToList();
                if (sameKind.Count > 1)
                {
                    tag += (sameKind.IndexOf(s) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var stem = SubtitleFileWriter.BuildStem(item, lang, s.IsForced, tag);
                if (!SubtitleFileWriter.Exists(item, stem))
                {
                    jobs.Add(new OcrJob(item, s, stem, StreamMap.Specifier(streams, s)));
                }
            }
        }

        return jobs;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed record OcrJob(Video Video, MediaStream Stream, string Stem, string Map);
}
