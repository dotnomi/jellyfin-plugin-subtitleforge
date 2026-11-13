using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubtitleForge.Common;
using Jellyfin.Plugin.SubtitleForge.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using EntityVideo = MediaBrowser.Controller.Entities.Video;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Exposes SubDL + OpenSubtitles (ranked by release match) in Jellyfin's manual "Search subtitles" dialog.
/// Downloads are normalised to clean SRT.
/// </summary>
public class ForgeSubtitleProvider : ISubtitleProvider
{
    private readonly SubtitleSearchService _search;
    private readonly ILibraryManager _libraryManager;
    private readonly SyncService _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForgeSubtitleProvider"/> class.
    /// </summary>
    /// <param name="search">Search service.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sync">Sync service.</param>
    public ForgeSubtitleProvider(SubtitleSearchService search, ILibraryManager libraryManager, SyncService sync)
    {
        _sync = sync;
        _search = search;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public string Name => "SubtitleForge";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Episode, VideoContentType.Movie };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var lang = Lang.ToTwoLetter(request.TwoLetterISOLanguageName) ?? Lang.ToTwoLetter(request.Language);
        if (lang is null)
        {
            return Array.Empty<RemoteSubtitleInfo>();
        }

        var video = _libraryManager.FindByPath(request.MediaPath, false) as EntityVideo;
        SubtitleQuery query;
        if (video is not null)
        {
            query = SubtitleSearchService.BuildQuery(video, new[] { lang });
        }
        else
        {
            query = new SubtitleQuery
            {
                IsEpisode = request.ContentType == VideoContentType.Episode,
                Title = request.ContentType == VideoContentType.Episode ? request.SeriesName : request.Name,
                Year = request.ProductionYear,
                Season = request.ParentIndexNumber,
                Episode = request.IndexNumber,
                ImdbId = request.ProviderIds.GetValueOrDefault("Imdb"),
                TmdbId = request.ProviderIds.GetValueOrDefault("Tmdb"),
                Languages = new[] { lang },
                Release = string.IsNullOrEmpty(request.MediaPath) ? null : ReleaseInfo.Parse(request.MediaPath),
            };
        }

        var ranked = await _search.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        return ranked.Select(c => new RemoteSubtitleInfo
        {
            Id = EncodeId(c, query, video?.Id),
            ProviderName = Name,
            Name = $"[{c.Source}] {c.DisplayName}",
            Comment = $"Score {c.Score} ({string.Join(", ", c.ScoreReasons)})" + (c.IsSafeMatch ? " – passt zum Release" : string.Empty),
            Author = c.Author,
            Format = "srt",
            ThreeLetterISOLanguageName = Lang.ToThreeLetter(c.Language),
            DownloadCount = c.Downloads,
            IsHashMatch = c.HashMatch,
            HearingImpaired = c.HearingImpaired,
            Forced = c.Forced,
            MachineTranslated = c.MachineTranslated,
        });
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var (source, reference, lang, season, episode, forced, itemId) = DecodeId(id);
        var candidate = new SubtitleCandidate { Source = source, Reference = reference, Language = lang, Forced = forced };
        var (data, fileName) = await _search.GetSource(source).DownloadAsync(candidate, cancellationToken).ConfigureAwait(false);
        var cues = SubtitleContent.Parse(data, fileName, season, episode)
            ?? throw new InvalidDataException("Downloaded subtitle contains no usable cues");

        // Correct the timing against the audio when we can be sure; otherwise deliver as-is (the user chose it).
        if (Plugin.Instance?.Configuration.SyncEnabled != false && itemId.HasValue && _libraryManager.GetItemById(itemId.Value) is EntityVideo video)
        {
            var alignment = await _sync.AlignAsync(video, cues, false, cancellationToken).ConfigureAwait(false);
            if (alignment is { IsConfident: true })
            {
                cues = alignment.Cues;
            }
        }
        return new SubtitleResponse
        {
            Format = "srt",
            Language = Lang.ToThreeLetter(lang),
            IsForced = forced,
            Stream = new MemoryStream(new UTF8Encoding(false).GetBytes(Srt.Write(cues))),
        };
    }

    private static string EncodeId(SubtitleCandidate c, SubtitleQuery q, Guid? itemId)
    {
        var raw = string.Join('\u001f', c.Source, c.Reference, c.Language, q.Season?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, q.Episode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, c.Forced ? "1" : "0", itemId?.ToString("N") ?? string.Empty);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static (string Source, string Reference, string Lang, int? Season, int? Episode, bool Forced, Guid? ItemId) DecodeId(string id)
    {
        var b64 = id.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        var p = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('\u001f');
        return (p[0], p[1], p[2], int.TryParse(p[3], out var s) ? s : null, int.TryParse(p[4], out var e) ? e : null, p[5] == "1", p.Length > 6 && Guid.TryParse(p[6], out var g) ? g : null);
    }
}
