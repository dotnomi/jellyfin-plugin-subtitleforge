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
    public ActionResult<object> Translate([FromRoute] Guid id, [FromQuery] string language, [FromQuery] int? sourceIndex = null)
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

        // A chosen source only makes sense for one movie/episode, and must be one of its translatable subtitles.
        if (sourceIndex is not null && (item is not Video single
            || Translation.SubtitleTranslator.Candidates(single.GetMediaStreams(), lang, true).All(c => c.Stream.Index != sourceIndex)))
        {
            return BadRequest(Loc.T("This subtitle cannot be translated into the chosen language.", "Dieser Untertitel kann nicht in die gewählte Sprache übersetzt werden."));
        }

        var due = DateTime.UtcNow;
        var queued = 0;
        foreach (var v in LibraryItems.VideosUnder(_libraryManager, item))
        {
            if (sourceIndex is null && Translation.SubtitleTranslator.PickSource(v.GetMediaStreams(), lang) is null)
            {
                continue;
            }

            _queue.Enqueue(new WorkItem(v.Id, WorkKind.Translate, lang, null, due, false, sourceIndex));
            due = due.AddTicks(1);
            queued++;
        }

        return Accepted(new { queued });
    }

    /// <summary>
    /// Subtitles of a movie/episode that can be translated into a language, best (the automatic choice) first.
    /// </summary>
    /// <param name="id">Video id.</param>
    /// <param name="language">Target language.</param>
    /// <returns>Candidates.</returns>
    [HttpGet("Items/{id}/TranslationSources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> TranslationSources([FromRoute] Guid id, [FromQuery] string language)
    {
        if (_libraryManager.GetItemById(id) is not Video video || Lang.ToTwoLetter(language) is not { } lang)
        {
            return NotFound();
        }

        // Whisper output and earlier translations come last and are never the automatic choice.
        return Ok(Translation.SubtitleTranslator.Candidates(video.GetMediaStreams(), lang, true).Select(c => new
        {
            index = c.Stream.Index,
            machine = c.Stream.IsExternal && Translation.SubtitleTranslator.IsMachineMade(c.Stream.Path!),
            language = c.Language,
            external = c.Stream.IsExternal,
            title = c.Stream.IsExternal ? System.IO.Path.GetFileName(c.Stream.Path) : (c.Stream.DisplayTitle ?? c.Stream.Title ?? c.Stream.Codec),
        }));
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
                    ? SubtitleContent.Parse(System.IO.File.ReadAllBytes(s.Path), s.Path, null, null) ?? new List<SubtitleCue>()
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
                canEdit = s.Path!.EndsWith(".srt", StringComparison.OrdinalIgnoreCase),
                index = s.Index,
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

        // A forced download ("forced.SubDL") has its own state ("forced:de"); its source is the part after "forced.".
        var forced = info.Tag.StartsWith("forced.", StringComparison.Ordinal);
        var sourceTag = forced ? info.Tag["forced.".Length..] : info.Tag;
        var stateKey = info.Origin switch
        {
            "download" => forced ? "forced:" + lang2 : lang2,
            "translation" => "translate:" + lang2,
            _ => "whisper:" + lang2,
        };
        var reference = info.Origin == "download" ? _state.Get(video.Id, stateKey)?.Reference : null;
        var snapshot = Snapshot(video.Id, stateKey);

        // Nothing is blocked, counted or searched again unless the file is really gone.
        if (System.IO.File.Exists(path) && !_writer.DeleteFile(video, path, "rejected", snapshot))
        {
            return BadRequest(Loc.T("The file could not be removed (permissions?).", "Die Datei konnte nicht entfernt werden (Rechte?)."));
        }

        var removed = 1;
        if (!forced)
        {
            removed += _writer.DeleteOwn(video, info.Language, tag => string.Equals(tag, info.Tag, StringComparison.Ordinal), null);
        }

        var blocked = false;
        if (info.Origin == "download")
        {
            SourceStats.Failed(sourceTag);
            if (reference is not null && reference.StartsWith(sourceTag + "|", StringComparison.OrdinalIgnoreCase))
            {
                // Forced candidates are checked against the block list of the plain language too.
                _state.Block(video.Id, lang2, reference);
                blocked = true;
            }

            _state.Set(video.Id, stateKey, "rejected", Loc.T("rejected by user – searching again", "vom Benutzer verworfen – suche neu"));
            _queue.Enqueue(new WorkItem(video.Id, WorkKind.Search, null, null, DateTime.UtcNow));
        }
        else if (info.Origin == "translation")
        {
            _state.Set(video.Id, stateKey, "rejected", Loc.T("rejected by user", "vom Benutzer verworfen"));
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
        var deletedKey = ItemProcessor.DeletedKey(System.IO.Path.GetFileNameWithoutExtension(path));
        var snapshot = key is null ? Snapshot(video.Id, deletedKey) : Snapshot(video.Id, key, deletedKey);
        if (!_writer.DeleteFile(video, path, "deleted", snapshot))
        {
            return BadRequest(Loc.T("The file could not be deleted (permissions?).", "Die Datei konnte nicht gelöscht werden (Rechte?)."));
        }

        if (key is not null)
        {
            _state.Set(video.Id, key, ItemProcessor.DeletedByUser, detail, _state.Get(video.Id, key)?.Reference);
        }

        _state.Set(video.Id, deletedKey, ItemProcessor.DeletedByUser, detail);
        return Ok(new { deleted = true, restorable = SubtitleTrash.List(video.Id).Any(e => e.FileName == file) });
    }

    /// <summary>
    /// Deleted or rejected subtitles of a video that can still be restored.
    /// </summary>
    /// <param name="id">Video id.</param>
    /// <returns>Entries, newest first.</returns>
    [HttpGet("Items/{id}/Trash")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Trash([FromRoute] Guid id) =>
        Ok(SubtitleTrash.List(id).Select(e => new { id = e.Id, file = e.FileName, deleted = e.DeletedUtc, reason = e.Reason }));

    /// <summary>
    /// All restorable subtitles (newest first, at most 500).
    /// </summary>
    /// <returns>Entries.</returns>
    [HttpGet("Trash")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> TrashAll() =>
        Ok(SubtitleTrash.List(null).Take(500).Select(e => new
        {
            id = e.Id,
            file = e.FileName,
            deleted = e.DeletedUtc,
            reason = e.Reason,
            item = e.ItemId,
            name = _libraryManager.GetItemById(e.ItemId) is { } item ? Title(item) : null,
        }));

    /// <summary>
    /// Puts a deleted or rejected subtitle back where it was, with the plugin state it had.
    /// </summary>
    /// <param name="entry">Recycle bin entry id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Result.</returns>
    [HttpPost("Trash/{entry}/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<object>> Restore([FromRoute] string entry, CancellationToken cancellationToken)
    {
        if (SubtitleTrash.Read(entry) is not { } stored || _libraryManager.GetItemById(stored.Entry.ItemId) is not Video video)
        {
            return NotFound();
        }

        var e = stored.Entry;
        var target = e.OriginalPath;
        if (!IsSubtitlePathOf(video, target) || OwnFile(video, target).Origin == "foreign")
        {
            return BadRequest(Loc.T("The original location is no longer valid (was the video moved?).", "Der ursprüngliche Ort ist nicht mehr gültig (wurde das Video verschoben?)."));
        }

        if (System.IO.File.Exists(target))
        {
            return Conflict(Loc.T("A file with this name exists again – delete it first.", "Eine Datei mit diesem Namen existiert wieder – lösche sie zuerst."));
        }

        await _writer.WriteFileAsync(video, target, stored.Content, cancellationToken).ConfigureAwait(false);
        foreach (var snap in e.States)
        {
            // Only undo what the delete/reject set: a newer result (e.g. the download found after a reject) stays.
            var now = _state.Get(video.Id, snap.Key)?.Outcome;
            if (now is "rejected" or ItemProcessor.DeletedByUser)
            {
                if (snap.Outcome is null)
                {
                    _state.Remove(video.Id, snap.Key);
                }
                else
                {
                    _state.Set(video.Id, snap.Key, snap.Outcome, snap.Detail, snap.Reference);
                }
            }

            if (e.Reason == "rejected" && snap.Reference is { } reference && (snap.Key.StartsWith("forced:", StringComparison.Ordinal) || !snap.Key.Contains(':', StringComparison.Ordinal)))
            {
                _state.Unblock(video.Id, snap.Key.StartsWith("forced:", StringComparison.Ordinal) ? snap.Key["forced:".Length..] : snap.Key, reference);
            }
        }

        SubtitleTrash.Forget(e.Id);
        return Ok(new { restored = true, file = e.FileName });
    }

    /// <summary>
    /// Shifts all cues of a subtitle file. Files SubtitleForge made are changed in place; for other files a shifted
    /// copy ("…Sync.srt") is written and the original stays untouched.
    /// </summary>
    /// <param name="id">Video id.</param>
    /// <param name="file">File name.</param>
    /// <param name="ms">Milliseconds (positive = later).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The file that now holds the shifted subtitle.</returns>
    [HttpPost("Items/{id}/Subtitles/Shift")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<object>> Shift([FromRoute] Guid id, [FromQuery] string file, [FromQuery] int ms, CancellationToken cancellationToken)
    {
        if (ms == 0 || Math.Abs(ms) > 600_000)
        {
            return BadRequest(Loc.T("The shift must be between -10 and +10 minutes.", "Die Verschiebung muss zwischen -10 und +10 Minuten liegen."));
        }

        if (_libraryManager.GetItemById(id) is not Video video)
        {
            return NotFound();
        }

        var path = ExternalSubtitle(video, file);
        if (path is null || !path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)
            || new System.IO.FileInfo(path).Length > SubtitleTrash.MaxFileBytes || new System.IO.FileInfo(path).LinkTarget is not null)
        {
            return NotFound();
        }

        // Files from elsewhere are often Windows-1252: decode like downloads, not as plain UTF-8.
        var cues = SubtitleContent.Parse(await System.IO.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), file, null, null);
        if (cues is null || cues.Count == 0)
        {
            return BadRequest(Loc.T("File contains no subtitles", "Datei enthält keine Untertitel"));
        }

        var shifted = Srt.Write(ShiftCues(cues, ms));
        var info = OwnFile(video, path);
        string written;
        if (info.Origin != "foreign")
        {
            await _writer.WriteFileAsync(video, path, new System.Text.UTF8Encoding(false).GetBytes(shifted), cancellationToken).ConfigureAwait(false);
            written = path;
        }
        else
        {
            written = await _writer.SaveAsync(video, CopyStem(video, path), shifted, cancellationToken).ConfigureAwait(false);
        }

        return Ok(new { file = System.IO.Path.GetFileName(written), ms, copy = info.Origin == "foreign" });
    }

    /// <summary>
    /// What SubtitleForge learned about the sources (saved vs. rejected downloads, ranking bonus).
    /// </summary>
    /// <returns>Rows.</returns>
    [HttpGet("Sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Sources() =>
        Ok(SourceStats.All().Select(r => new { source = r.Source, saved = r.Saved, failed = r.Failed, bonus = r.Bonus }));

    /// <summary>
    /// Shifts cues, dropping those that would end before 0.
    /// </summary>
    /// <param name="cues">Cues.</param>
    /// <param name="ms">Shift.</param>
    /// <returns>Shifted cues.</returns>
    internal static List<SubtitleCue> ShiftCues(IEnumerable<SubtitleCue> cues, int ms) =>
        cues.Select(c => c with { StartMs = Math.Max(0, c.StartMs + ms), EndMs = c.EndMs + ms })
            .Where(c => c.EndMs > c.StartMs)
            .ToList();

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

        var path = ExternalSubtitle(video, file);
        if (path is null || !path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)
            || new System.IO.FileInfo(path).LinkTarget is not null || new System.IO.FileInfo(path).Length > SubtitleTrash.MaxFileBytes)
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
        if (info.Origin != "foreign")
        {
            await _writer.WriteFileAsync(video, path, new System.Text.UTF8Encoding(false).GetBytes(Srt.Write(alignment.Cues)), cancellationToken).ConfigureAwait(false);
            written = path;
        }
        else
        {
            written = await _writer.SaveAsync(video, CopyStem(video, path), Srt.Write(alignment.Cues), cancellationToken).ConfigureAwait(false);
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

    // An external subtitle of the video by file name. A plugin file written a moment ago (e.g. a corrected copy) is
    // found on disk too, before Jellyfin's refresh lists it as a stream.
    private string? ExternalSubtitle(Video video, string file)
    {
        var listed = video.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path))
            .Select(s => s.Path!)
            .FirstOrDefault(p => string.Equals(System.IO.Path.GetFileName(p), file, StringComparison.Ordinal));
        if (listed is not null || string.IsNullOrEmpty(file) || file != System.IO.Path.GetFileName(file))
        {
            return listed;
        }

        return new[] { video.ContainingFolderPath, video.GetInternalMetadataPath() }
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => System.IO.Path.Combine(d, file))
            .FirstOrDefault(p => System.IO.File.Exists(p) && IsSubtitlePathOf(video, p) && OwnFile(video, p).Origin != "foreign");
    }

    // Name of the corrected copy of a file from elsewhere: same language and forced flag, plus what else its name says
    // ("Movie.de.sdh.srt" → "Movie.de.[SF] Sync sdh.srt"), so copies of different files do not replace each other.
    private static string CopyStem(Video video, string path)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var prefix = System.IO.Path.GetFileNameWithoutExtension(video.Path) + ".";
        var parts = (stem.StartsWith(prefix, StringComparison.Ordinal) ? stem[prefix.Length..] : string.Empty)
            .Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        var stream = video.GetMediaStreams().FirstOrDefault(s => s.IsExternal && string.Equals(s.Path, path, StringComparison.Ordinal));
        var forced = stream?.IsForced == true || parts.Contains("forced", StringComparer.OrdinalIgnoreCase);
        var language = Lang.FileToken(stream?.Language ?? parts.FirstOrDefault());
        var extra = string.Join(' ', parts.Skip(1).Where(p => !p.Equals("forced", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("default", StringComparison.OrdinalIgnoreCase) && p.All(char.IsLetterOrDigit)).Take(3));
        var tag = extra.Length > 0 && extra.Length <= 30 ? "Sync " + extra : "Sync";
        try
        {
            return SubtitleFileWriter.BuildStem(video, language, forced, tag);
        }
        catch (ArgumentException)
        {
            return SubtitleFileWriter.BuildStem(video, language, forced, "Sync");
        }
    }

    // A restore target must be an .srt of this video, directly in its folder or its metadata folder.
    private static bool IsSubtitlePathOf(Video video, string path)
    {
        if (!path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || !System.IO.Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var full = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(full);
        var name = System.IO.Path.GetFileName(full);
        var stem = System.IO.Path.GetFileNameWithoutExtension(video.Path) + ".";
        return name.StartsWith(stem, StringComparison.Ordinal)
            && new[] { video.ContainingFolderPath, video.GetInternalMetadataPath() }
                .Where(d => !string.IsNullOrEmpty(d))
                .Any(d => string.Equals(System.IO.Path.GetFullPath(d), dir, StringComparison.Ordinal));
    }

    private List<SubtitleTrash.StateSnapshot> Snapshot(Guid itemId, params string[] keys) =>
        keys.Distinct(StringComparer.Ordinal).Select(k => _state.Get(itemId, k) is { } e
                ? new SubtitleTrash.StateSnapshot { Key = k, Outcome = e.Outcome, Detail = e.Detail, Reference = e.Reference }
                : new SubtitleTrash.StateSnapshot { Key = k })
            .ToList();

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
