using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubtitleForge.Api;

/// <summary>
/// Overview and manual actions for administrators.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("SubtitleForge")]
public class SubtitleForgeController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly LanguageResolver _languages;
    private readonly DownloadStateStore _state;
    private readonly WorkQueue _queue;
    private readonly SubtitleSearchService _search;
    private readonly SubtitleFileWriter _writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleForgeController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="languages">Language resolver.</param>
    /// <param name="state">State store.</param>
    /// <param name="queue">Work queue.</param>
    /// <param name="search">Search service.</param>
    /// <param name="writer">Subtitle writer.</param>
    public SubtitleForgeController(ILibraryManager libraryManager, LanguageResolver languages, DownloadStateStore state, WorkQueue queue, SubtitleSearchService search, SubtitleFileWriter writer)
    {
        _search = search;
        _writer = writer;
        _libraryManager = libraryManager;
        _languages = languages;
        _state = state;
        _queue = queue;
    }

    /// <summary>
    /// Per-item overview: wanted languages, what exists, and what happened when something is missing.
    /// </summary>
    /// <param name="onlyMissing">Only items with at least one missing language.</param>
    /// <returns>Rows.</returns>
    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<ReportRow>> Report([FromQuery] bool onlyMissing = false)
    {
        var rows = new List<ReportRow>();
        foreach (var video in LibraryItems.Videos(_libraryManager))
        {
            var wanted = _languages.Resolve(video, out var source);
            var streams = video.GetMediaStreams();
            var langs = new List<ReportLanguage>();
            foreach (var lang in wanted)
            {
                var have = streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && Lang.Same(s.Language, lang))
                    .Select(Describe).ToList();
                var download = _state.Get(video.Id, lang);
                var whisper = _state.Get(video.Id, "whisper:" + lang);
                langs.Add(new ReportLanguage
                {
                    Language = lang,
                    Have = have,
                    Outcome = have.Count > 0 ? "ok" : download?.Outcome ?? "pending",
                    Detail = have.Count > 0 ? null : download?.Detail,
                    LastTried = download?.LastTriedUtc,
                    Whisper = whisper is null ? null : whisper.Outcome + ": " + whisper.Detail,
                });
            }

            if (onlyMissing && langs.All(l => l.Have.Count > 0))
            {
                continue;
            }

            rows.Add(new ReportRow
            {
                Id = video.Id,
                Name = Title(video),
                LanguageSource = source,
                WhisperAuto = LanguageResolver.ResolveWhisperAuto(video, out var whisperSource) ? Loc.T("automatic (", "automatisch (") + whisperSource + ")" : Loc.T("manual (", "manuell (") + whisperSource + ")",
                Languages = langs,
                AudioTracks = streams.Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal).OrderBy(s => s.Index)
                    .Select((s, i) => new ReportAudio { Index = i, Language = s.Language, Title = s.DisplayTitle }).ToList(),
            });
        }

        return Ok(rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Searches/downloads again, ignoring the retry delay. For a season, series or library: all episodes/movies in it.
    /// </summary>
    /// <param name="id">Item id (video or folder).</param>
    /// <returns>Number of queued jobs.</returns>
    [HttpPost("Items/{id}/Search")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> Search([FromRoute] Guid id)
    {
        if (_libraryManager.GetItemById(id) is not { } item)
        {
            return NotFound();
        }

        var videos = LibraryItems.VideosUnder(_libraryManager, item);
        var due = DateTime.UtcNow;
        foreach (var v in videos)
        {
            _queue.Enqueue(new WorkItem(v.Id, WorkKind.Search, null, null, due));
            due = due.AddTicks(1); // keep episode order
        }

        return Accepted(new { queued = videos.Count });
    }

    /// <summary>
    /// Generates subtitles with Whisper. For a season, series or library: every episode/movie in it.
    /// </summary>
    /// <param name="id">Item id (video or folder).</param>
    /// <param name="language">Target language (ISO 639-1).</param>
    /// <param name="audioIndex">Position of the audio track (0 = first audio track); single videos only.</param>
    /// <param name="audioLanguage">Audio language to use (per episode the first track in that language).</param>
    /// <param name="replace">Replace SubtitleForge's own downloaded subtitle of that language (and never download it again).</param>
    /// <returns>Number of queued jobs.</returns>
    [HttpPost("Items/{id}/Whisper")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> Whisper([FromRoute] Guid id, [FromQuery] string language, [FromQuery] int? audioIndex, [FromQuery] string? audioLanguage, [FromQuery] bool replace = false)
    {
        var lang = Lang.ToTwoLetter(language);
        if (lang is null)
        {
            return BadRequest("unknown language");
        }

        if (_libraryManager.GetItemById(id) is not { } item)
        {
            return NotFound();
        }

        var videos = LibraryItems.VideosUnder(_libraryManager, item);
        var due = DateTime.UtcNow;
        var queued = 0;
        foreach (var v in videos)
        {
            var index = videos.Count == 1 ? audioIndex : null;
            if (!string.IsNullOrEmpty(audioLanguage))
            {
                var tracks = v.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal).OrderBy(s => s.Index).ToList();
                var pos = tracks.FindIndex(s => Lang.Same(s.Language, audioLanguage));
                if (pos < 0)
                {
                    continue; // episode has no track in that language
                }

                index = pos;
            }

            _queue.Enqueue(new WorkItem(v.Id, WorkKind.Whisper, lang, index, due, replace));
            due = due.AddTicks(1);
            queued++;
        }

        return Accepted(new { queued });
    }

    /// <summary>
    /// Translates an existing subtitle of another language offline. For a season, series or library: every video.
    /// </summary>
    /// <param name="id">Item id (video or folder).</param>
    /// <param name="language">Target language (ISO 639-1).</param>
    /// <returns>Number of queued jobs.</returns>
    [HttpPost("Items/{id}/Translate")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> Translate([FromRoute] Guid id, [FromQuery] string language)
    {
        var lang = Lang.ToTwoLetter(language);
        if (lang is null)
        {
            return BadRequest("unknown language");
        }

        if (_libraryManager.GetItemById(id) is not { } item)
        {
            return NotFound();
        }

        var due = DateTime.UtcNow;
        var queued = 0;
        foreach (var v in LibraryItems.VideosUnder(_libraryManager, item))
        {
            if (Translation.SubtitleTranslator.PickSource(v.GetMediaStreams(), lang) is null)
            {
                continue;
            }

            _queue.Enqueue(new WorkItem(v.Id, WorkKind.Translate, lang, null, due));
            due = due.AddTicks(1);
            queued++;
        }

        return Accepted(new { queued });
    }

    /// <summary>
    /// Information for the action dialog on an item's detail page.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>Kind, number of videos, audio languages, wanted languages and per-language status.</returns>
    [HttpGet("Items/{id}/Info")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> Info([FromRoute] Guid id)
    {
        if (_libraryManager.GetItemById(id) is not { } item)
        {
            return NotFound();
        }

        var videos = LibraryItems.VideosUnder(_libraryManager, item);
        var audio = videos.Take(50)
            .SelectMany(v => v.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal))
            .Select(s => Lang.ToTwoLetter(s.Language ?? string.Empty))
            .Where(l => l is not null)
            .Distinct()
            .ToList();
        var first = videos.FirstOrDefault();
        return Ok(new
        {
            kind = item is Video ? "video" : "folder",
            name = Title(item),
            videos = videos.Count,
            audioLanguages = audio,
            audioTracks = item is Video single
                ? single.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal).OrderBy(s => s.Index)
                    .Select((s, i) => new ReportAudio { Index = i, Language = s.Language, Title = s.DisplayTitle }).ToList()
                : new List<ReportAudio>(),
            languages = first is null ? new List<string>() : _languages.Resolve(first, out _).ToList(),
        });
    }

    /// <summary>
    /// External subtitle files of a video with a short preview (first cues), and who made them.
    /// </summary>
    /// <param name="id">Video id.</param>
    /// <returns>Files.</returns>
    [HttpGet("Items/{id}/Subtitles")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> Subtitles([FromRoute] Guid id)
    {
        if (_libraryManager.GetItemById(id) is not Video video)
        {
            return NotFound();
        }

        var files = new List<object>();
        foreach (var s in video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path)))
        {
            var info = OwnFile(video, s.Path!);
            List<SubtitleCue> cues;
            try
            {
                cues = s.Path!.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(s.Path) && new System.IO.FileInfo(s.Path).Length < 20_000_000
                    ? Srt.Parse(System.IO.File.ReadAllText(s.Path))
                    : new List<SubtitleCue>();
            }
            catch (System.IO.IOException)
            {
                cues = new List<SubtitleCue>();
            }

            // Preview from the middle of the file: the first cues are often credits or signs.
            var start = Math.Max(0, (cues.Count / 3) - 3);
            files.Add(new
            {
                file = System.IO.Path.GetFileName(s.Path),
                language = s.Language,
                title = s.Title,
                forced = s.IsForced,
                origin = info.Origin,
                canReject = info.Origin is "download" or "whisper" or "translation",
                canDelete = info.Origin != "foreign",
                count = cues.Count,
                preview = cues.Skip(start).Take(6).Select(c => new { time = Srt.FormatTime(c.StartMs)[..8], text = c.Text.Replace("\n", " / ", StringComparison.Ordinal) }),
            });
        }

        return Ok(files);
    }

    /// <summary>
    /// "Doesn't fit": removes a subtitle SubtitleForge made. A download is also blocked (never chosen again for this
    /// item) and a new search is queued; a Whisper file is just removed.
    /// </summary>
    /// <param name="id">Video id.</param>
    /// <param name="file">File name (as returned by <see cref="Subtitles"/>).</param>
    /// <returns>Result.</returns>
    [HttpPost("Items/{id}/Subtitles/Reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> Reject([FromRoute] Guid id, [FromQuery] string file)
    {
        if (_libraryManager.GetItemById(id) is not Video video)
        {
            return NotFound();
        }

        var path = video.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path))
            .Select(s => s.Path!)
            .FirstOrDefault(p => string.Equals(System.IO.Path.GetFileName(p), file, StringComparison.Ordinal));
        if (path is null)
        {
            return NotFound();
        }

        var info = OwnFile(video, path);
        if (info.Origin is not ("download" or "whisper" or "translation") || info.Language is null)
        {
            return BadRequest(Loc.T("Only the plugin's downloads and Whisper subtitles can be rejected.", "Nur Downloads und Whisper-Untertitel des Plugins können verworfen werden."));
        }

        var lang2 = Lang.ToTwoLetter(info.Language) ?? info.Language;
        var blocked = false;
        if (info.Origin == "download" && _state.Get(video.Id, lang2)?.Reference is { } reference
            && reference.StartsWith(info.Tag + "|", StringComparison.OrdinalIgnoreCase))
        {
            _state.Block(video.Id, lang2, reference);
            blocked = true;
        }

        var removed = _writer.DeleteOwn(video, info.Language, tag => string.Equals(tag, info.Tag, StringComparison.Ordinal), null);
        if (info.Origin == "translation")
        {
            _state.Set(video.Id, "translate:" + lang2, "rejected", Loc.T("rejected by user", "vom Benutzer verworfen"));
        }
        if (info.Origin == "download")
        {
            _state.Set(video.Id, lang2, "rejected", Loc.T("rejected by user – searching again", "vom Benutzer verworfen – suche neu"));
            _queue.Enqueue(new WorkItem(video.Id, WorkKind.Search, null, null, DateTime.UtcNow));
        }

        return Ok(new { removed, blocked, searching = info.Origin == "download" });
    }

    /// <summary>
    /// Deletes one subtitle file SubtitleForge made (download, translation, Whisper, OCR, converted). Embedded tracks
    /// and files from elsewhere cannot be deleted. Automatic runs do not create the same subtitle again; a manual
    /// action still can.
    /// </summary>
    /// <param name="id">Video id.</param>
    /// <param name="file">File name (as returned by <see cref="Subtitles"/>).</param>
    /// <returns>Result.</returns>
    [HttpPost("Items/{id}/Subtitles/Delete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> Delete([FromRoute] Guid id, [FromQuery] string file)
    {
        if (_libraryManager.GetItemById(id) is not Video video)
        {
            return NotFound();
        }

        var path = video.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path))
            .Select(s => s.Path!)
            .FirstOrDefault(p => string.Equals(System.IO.Path.GetFileName(p), file, StringComparison.Ordinal));
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        var info = OwnFile(video, path);
        if (info.Origin == "foreign" || info.Language is null)
        {
            return BadRequest(Loc.T("Only subtitles SubtitleForge created can be deleted.", "Nur Untertitel, die SubtitleForge erstellt hat, können gelöscht werden."));
        }

        if (!_writer.DeleteFile(video, path))
        {
            return BadRequest(Loc.T("The file could not be deleted (permissions?).", "Die Datei konnte nicht gelöscht werden (Rechte?)."));
        }

        var lang2 = Lang.ToTwoLetter(info.Language) ?? info.Language;
        var forced = info.Tag.StartsWith("forced.", StringComparison.Ordinal);
        var detail = Loc.T("deleted by user", "vom Benutzer gelöscht");
        var key = info.Origin switch
        {
            "download" => forced ? "forced:" + lang2 : lang2,
            "translation" => "translate:" + lang2,
            "whisper" => "whisper:" + lang2,
            _ => null,
        };
        if (key is not null)
        {
            _state.Set(video.Id, key, ItemProcessor.DeletedByUser, detail, _state.Get(video.Id, key)?.Reference);
        }

        _state.Set(video.Id, ItemProcessor.DeletedKey(System.IO.Path.GetFileNameWithoutExtension(path)), ItemProcessor.DeletedByUser, detail);
        return Ok(new { deleted = true });
    }

    /// <summary>
    /// The last sync report (scheduled task "Sync-Bericht erstellen").
    /// </summary>
    /// <returns>Rows, problems first.</returns>
    [HttpGet("SyncReport")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> SyncReport()
    {
        var order = new Dictionary<string, int>(StringComparer.Ordinal) { ["shifted"] = 0, ["unsure"] = 1, ["error"] = 2, ["no-audio"] = 3, ["empty"] = 4, ["ok"] = 5 };
        var rows = Jellyfin.Plugin.SubtitleForge.Sync.SyncReportTask.Load();
        return Ok(new
        {
            created = Jellyfin.Plugin.SubtitleForge.Sync.SyncReportTask.ReportPath is { } p && System.IO.File.Exists(p) ? System.IO.File.GetLastWriteTime(p) : (DateTime?)null,
            rows = rows.OrderBy(r => order.GetValueOrDefault(r.Outcome, 9)).ThenBy(r => r.Name, StringComparer.CurrentCulture),
        });
    }

    /// <summary>
    /// Corrects the timing of one subtitle file. Files SubtitleForge made are corrected in place; for other files a
    /// corrected copy ("…Sync.srt") is written next to it and the original stays untouched.
    /// </summary>
    /// <param name="id">Video id.</param>
    /// <param name="file">File name.</param>
    /// <param name="sync">Sync service.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Result.</returns>
    [HttpPost("Items/{id}/Subtitles/Fix")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> Fix([FromRoute] Guid id, [FromQuery] string file, [FromServices] Jellyfin.Plugin.SubtitleForge.Sync.SyncService sync, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(id) is not Video video)
        {
            return NotFound();
        }

        var path = video.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path))
            .Select(s => s.Path!)
            .FirstOrDefault(p => string.Equals(System.IO.Path.GetFileName(p), file, StringComparison.Ordinal));
        if (path is null || !path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var cues = SubtitleContent.Parse(await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), file, null, null);
        if (cues is null || cues.Count < 3)
        {
            return BadRequest(Loc.T("File contains no subtitles", "Datei enthält keine Untertitel"));
        }

        var alignment = await sync.AlignAsync(video, cues, false, cancellationToken).ConfigureAwait(false);
        if (alignment is null || !alignment.IsConfident)
        {
            return Ok(new { ok = false, message = Loc.T("Timing could not be determined reliably – the subtitle may belong to a different version", "Timing nicht sicher bestimmbar – evtl. gehört der Untertitel zu einer anderen Fassung") });
        }

        var info = OwnFile(video, path);
        string written;
        if (info.Origin is "download" or "plugin")
        {
            await SubtitleFileWriter.WriteAtomicAsync(path, new System.Text.UTF8Encoding(false).GetBytes(Srt.Write(alignment.Cues)), cancellationToken).ConfigureAwait(false);
            written = path;
        }
        else
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var prefix = System.IO.Path.GetFileNameWithoutExtension(video.Path) + ".";
            var rest = stem.StartsWith(prefix, StringComparison.Ordinal) ? stem[prefix.Length..] : string.Empty;
            var lang = rest.Split('.')[0];
            lang = Lang.ToTwoLetter(lang) is null ? "und" : lang;
            written = await _writer.SaveAsync(video, SubtitleFileWriter.BuildStem(video, lang, false, "Sync"), Srt.Write(alignment.Cues), cancellationToken).ConfigureAwait(false);
        }

        var rows = Jellyfin.Plugin.SubtitleForge.Sync.SyncReportTask.Load();
        foreach (var r in rows.Where(r => r.ItemId == id && r.File == file))
        {
            r.Fixed = true;
        }

        Jellyfin.Plugin.SubtitleForge.Sync.SyncReportTask.Save(rows);
        return Ok(new { ok = true, message = Loc.T("fixed", "korrigiert") + $" ({alignment}) → {System.IO.Path.GetFileName(written)}" });
    }

    /// <summary>
    /// Coverage per library and wanted language.
    /// </summary>
    /// <returns>Statistics.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Stats()
    {
        var libs = new Dictionary<string, (int Videos, Dictionary<string, (int Wanted, int Have, int Own)> Langs)>(StringComparer.Ordinal);
        foreach (var video in LibraryItems.Videos(_libraryManager))
        {
            var lib = _libraryManager.GetCollectionFolders(video).FirstOrDefault()?.Name ?? "?";
            if (!libs.TryGetValue(lib, out var entry))
            {
                entry = (0, new Dictionary<string, (int, int, int)>(StringComparer.Ordinal));
            }

            entry.Videos++;
            var streams = video.GetMediaStreams().Where(s => s.Type == MediaStreamType.Subtitle && !s.IsForced).ToList();
            foreach (var lang in _languages.Resolve(video, out _))
            {
                var matching = streams.Where(s => Lang.Same(s.Language, lang)).ToList();
                var own = matching.Any(s => s.IsExternal && !string.IsNullOrEmpty(s.Path) && OwnFile(video, s.Path!).Origin != "foreign");
                entry.Langs.TryGetValue(lang, out var c);
                entry.Langs[lang] = (c.Wanted + 1, c.Have + (matching.Count > 0 ? 1 : 0), c.Own + (own ? 1 : 0));
            }

            libs[lib] = entry;
        }

        return Ok(libs.OrderBy(l => l.Key, StringComparer.CurrentCulture).Select(l => new
        {
            library = l.Key,
            videos = l.Value.Videos,
            languages = l.Value.Langs.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
            {
                language = x.Key,
                wanted = x.Value.Wanted,
                have = x.Value.Have,
                byPlugin = x.Value.Own,
                percent = x.Value.Wanted == 0 ? 0 : (int)Math.Round(100.0 * x.Value.Have / x.Value.Wanted),
            }),
        }));
    }

    /// <summary>
    /// Hardware status for Whisper (GPU detection and the runtime in use).
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Hardware")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Hardware()
    {
        var check = Jellyfin.Plugin.SubtitleForge.Whisper.GpuDetector.Check();
        var engine = Jellyfin.Plugin.SubtitleForge.Whisper.WhisperEngine.GpuBroken;
        return Ok(new
        {
            gpu = engine ? Jellyfin.Plugin.SubtitleForge.Whisper.WhisperEngine.GpuStatus : check.Description,
            gpuUsable = check.Usable && !engine,
            runtime = Jellyfin.Plugin.SubtitleForge.Whisper.WhisperEngine.LoadedRuntime,
            selfTest = Jellyfin.Plugin.SubtitleForge.Whisper.GpuSelfTest.LastResult,
        });
    }

    /// <summary>
    /// Runs the GPU self-test now (downloads the configured Whisper model if needed).
    /// </summary>
    /// <param name="selfTest">Self-test service.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Result text.</returns>
    [HttpPost("Hardware/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> HardwareTest([FromServices] Jellyfin.Plugin.SubtitleForge.Whisper.GpuSelfTest selfTest, CancellationToken cancellationToken)
    {
        var result = await selfTest.RunAsync(true, cancellationToken).ConfigureAwait(false);
        return Ok(new { result });
    }

    /// <summary>
    /// Sends a test notification (activity log / webhook).
    /// </summary>
    /// <param name="notifier">Notifier.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>No content.</returns>
    [HttpPost("Notify/Test")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> NotifyTest([FromServices] Notifier notifier, CancellationToken cancellationToken)
    {
        await notifier.TestAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Queue status with progress and remaining time, plus running SubtitleForge scheduled tasks.
    /// </summary>
    /// <param name="taskManager">Task manager.</param>
    /// <returns>Status.</returns>
    [HttpGet("Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Queue([FromServices] MediaBrowser.Model.Tasks.ITaskManager taskManager)
    {
        string Name(Guid id) => _libraryManager.GetItemById(id) is { } item ? Title(item) : id.ToString("N");
        var pending = _queue.Pending();
        var currentRemaining = _queue.CurrentRemainingSeconds();
        var pendingRows = pending.Select(p => new { name = Name(p.ItemId), kind = p.Kind.ToString(), language = p.Language, due = p.DueUtc, etaSeconds = (int)_queue.Estimate(p) }).ToList();

        var tasks = taskManager.ScheduledTasks
            .Where(t => t.Category == "SubtitleForge" && t.State == MediaBrowser.Model.Tasks.TaskState.Running)
            .Select(t =>
            {
                // ScheduledTaskWorker exposes no start time of the running execution; TaskEta derives it from our clock.
                var progress = t.CurrentProgress is { } cp ? cp / 100.0 : (double?)null;
                return new { name = t.Name, progress, etaSeconds = TaskEta(t.Id, progress) };
            })
            .ToList();

        var total = (currentRemaining ?? 0) + pendingRows.Sum(p => p.etaSeconds) + tasks.Sum(t => t.etaSeconds ?? 0);
        return Ok(new
        {
            current = _queue.Current is { } c
                ? new
                {
                    name = Name(c.ItemId),
                    kind = c.Kind.ToString(),
                    language = c.Language,
                    progress = _queue.CurrentProgress,
                    elapsedSeconds = (int)(DateTime.UtcNow - _queue.CurrentStartedUtc).TotalSeconds,
                    etaSeconds = currentRemaining is { } r ? (int)r : (int?)null,
                    waiting = _queue.CurrentWaiting,
                }
                : null,
            pending = pendingRows,
            tasks,
            totalEtaSeconds = (int)total,
            log = _queue.Log.Reverse().ToList(),
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Start, double Progress)> TaskStarts = new();

    /// <summary>
    /// Remaining time of a running scheduled task, from its progress since we first saw it running.
    /// </summary>
    private static int? TaskEta(string id, double? progress)
    {
        if (progress is not { } p)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var first = TaskStarts.GetOrAdd(id, _ => (now, p));
        if (p < first.Progress)
        {
            TaskStarts[id] = first = (now, p); // a new run started
        }

        var done = p - first.Progress;
        var elapsed = (now - first.Start).TotalSeconds;
        return done < 0.01 || elapsed < 10 ? null : (int)(elapsed * (1 - p) / done);
    }

    private (string Origin, string? Language, string Tag) OwnFile(Video video, string path)
    {
        var o = SubtitleOrigin.Classify(video.Path, path, _search.Sources.Select(x => x.Name));
        return (o.Origin, o.Language, o.Tag);
    }

    private static string Title(BaseItem item) => item is Episode e
        ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00} – {e.Name}"
        : item.ProductionYear is { } y && !item.Name.Contains($"({y})", StringComparison.Ordinal) ? $"{item.Name} ({y})" : item.Name;

    private static string Describe(MediaStream s)
    {
        if (!s.IsExternal)
        {
            return s.IsTextSubtitleStream ? Loc.T("embedded", "eingebettet") : Loc.T("embedded (image)", "eingebettet (Bild)");
        }

        return string.IsNullOrEmpty(s.Title) ? Loc.T("external", "extern") : s.Title!;
    }

    /// <summary>One item in the overview.</summary>
    public sealed class ReportRow
    {
        /// <summary>Gets or sets the item id.</summary>
        public Guid Id { get; set; }

        /// <summary>Gets or sets the display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets where the language setting came from.</summary>
        public string LanguageSource { get; set; } = string.Empty;

        /// <summary>Gets or sets whether Whisper runs automatically for this item and why.</summary>
        public string WhisperAuto { get; set; } = string.Empty;

        /// <summary>Gets or sets the per-language status.</summary>
        public List<ReportLanguage> Languages { get; set; } = new();

        /// <summary>Gets or sets the audio tracks (for manual Whisper).</summary>
        public List<ReportAudio> AudioTracks { get; set; } = new();
    }

    /// <summary>Status of one wanted language.</summary>
    public sealed class ReportLanguage
    {
        /// <summary>Gets or sets the language.</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>Gets or sets the existing subtitles (source descriptions).</summary>
        public List<string> Have { get; set; } = new();

        /// <summary>Gets or sets the outcome (ok, pending, none-found, no-safe-match, rejected-sync, …).</summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>Gets or sets details.</summary>
        public string? Detail { get; set; }

        /// <summary>Gets or sets the last attempt.</summary>
        public DateTime? LastTried { get; set; }

        /// <summary>Gets or sets the Whisper status.</summary>
        public string? Whisper { get; set; }
    }

    /// <summary>An audio track.</summary>
    public sealed class ReportAudio
    {
        /// <summary>Gets or sets the position among the audio tracks (0 = first).</summary>
        public int Index { get; set; }

        /// <summary>Gets or sets the language.</summary>
        public string? Language { get; set; }

        /// <summary>Gets or sets the title.</summary>
        public string? Title { get; set; }
    }
}
