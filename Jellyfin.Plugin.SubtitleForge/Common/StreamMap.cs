using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Builds ffmpeg stream specifiers. Jellyfin's <see cref="MediaStream.Index"/> counts external subtitle files first,
/// so it differs from the container index as soon as external subtitles exist. A type-relative specifier
/// ("0:a:1", "0:s:0") over the embedded streams is always correct.
/// </summary>
public static class StreamMap
{
    /// <summary>
    /// Gets the ffmpeg map specifier for an embedded stream.
    /// </summary>
    /// <param name="all">All media streams of the item.</param>
    /// <param name="stream">The stream.</param>
    /// <returns>Specifier like "0:a:1".</returns>
    public static string Specifier(IReadOnlyList<MediaStream> all, MediaStream stream)
    {
        var letter = stream.Type switch
        {
            MediaStreamType.Audio => "a",
            MediaStreamType.Subtitle => "s",
            MediaStreamType.Video => "v",
            _ => "d",
        };
        var position = all
            .Where(s => s.Type == stream.Type && !s.IsExternal)
            .OrderBy(s => s.Index)
            .ToList()
            .FindIndex(s => s.Index == stream.Index);
        return "0:" + letter + ":" + position.ToString(CultureInfo.InvariantCulture);
    }
}
