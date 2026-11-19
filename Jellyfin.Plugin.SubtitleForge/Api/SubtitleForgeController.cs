using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleForgeController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="languages">Language resolver.</param>
    /// <param name="state">State store.</param>
    /// <param name="queue">Work queue.</param>
    public SubtitleForgeController(ILibraryManager libraryManager, LanguageResolver languages, DownloadStateStore state, WorkQueue queue)
    {
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
                WhisperAuto = LanguageResolver.ResolveWhisperAuto(video, out var whisperSource) ? "automatisch (" + whisperSource + ")" : "manuell (" + whisperSource + ")",
                Languages = langs,
                AudioTracks = streams.Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal).OrderBy(s => s.Index)
                    .Select((s, i) => new ReportAudio { Index = i, Language = s.Language, Title = s.DisplayTitle }).ToList(),
            });
        }

        return Ok(rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Searches/downloads again for an item, ignoring the retry delay.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>Accepted.</returns>
    [HttpPost("Items/{id}/Search")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult Search([FromRoute] Guid id)
    {
        _queue.Enqueue(new WorkItem(id, WorkKind.Search, null, null, DateTime.UtcNow));
        return Accepted();
    }

    /// <summary>
    /// Generates a subtitle with Whisper.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <param name="language">Target language (ISO 639-1).</param>
    /// <param name="audioIndex">Position of the audio track (0 = first audio track), or empty for automatic choice.</param>
    /// <param name="replace">Replace SubtitleForge's own downloaded subtitle of that language (and never download it again).</param>
    /// <returns>Accepted.</returns>
    [HttpPost("Items/{id}/Whisper")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult Whisper([FromRoute] Guid id, [FromQuery] string language, [FromQuery] int? audioIndex, [FromQuery] bool replace = false)
    {
        var lang = Lang.ToTwoLetter(language);
        if (lang is null)
        {
            return BadRequest("unknown language");
        }

        _queue.Enqueue(new WorkItem(id, WorkKind.Whisper, lang, audioIndex, DateTime.UtcNow, replace));
        return Accepted();
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
        return Ok(new { gpu = check.Description, gpuUsable = check.Usable, runtime = Jellyfin.Plugin.SubtitleForge.Whisper.WhisperEngine.LoadedRuntime });
    }

    /// <summary>
    /// Queue status.
    /// </summary>
    /// <returns>Status.</returns>
    [HttpGet("Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Queue()
    {
        string Name(Guid id) => _libraryManager.GetItemById(id) is { } item ? Title(item) : id.ToString("N");
        return Ok(new
        {
            current = _queue.Current is { } c ? new { name = Name(c.ItemId), kind = c.Kind.ToString(), language = c.Language } : null,
            pending = _queue.Pending().Select(p => new { name = Name(p.ItemId), kind = p.Kind.ToString(), language = p.Language, due = p.DueUtc }),
            log = _queue.Log.Reverse().ToList(),
        });
    }

    private static string Title(BaseItem item) => item is Episode e
        ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00} – {e.Name}"
        : item.ProductionYear is { } y && !item.Name.Contains($"({y})", StringComparison.Ordinal) ? $"{item.Name} ({y})" : item.Name;

    private static string Describe(MediaStream s)
    {
        if (!s.IsExternal)
        {
            return s.IsTextSubtitleStream ? "eingebettet" : "eingebettet (Bild)";
        }

        return string.IsNullOrEmpty(s.Title) ? "extern" : s.Title!;
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
