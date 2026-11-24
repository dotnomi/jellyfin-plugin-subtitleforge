using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Who made an external subtitle file, judged by its name ("Video.lang[.forced].[SF] Tag.srt").
/// </summary>
/// <param name="Origin">download, translation, whisper, ocr, plugin or foreign.</param>
/// <param name="Language">Language token, or null.</param>
/// <param name="Tag">Tag without the SubtitleForge mark, with a leading "forced." if forced.</param>
/// <param name="Branded">Whether the name already carries the SubtitleForge mark.</param>
public sealed partial record SubtitleOrigin(string Origin, string? Language, string Tag, bool Branded)
{
    /// <summary>Download sources whose names are used as tags.</summary>
    public static readonly IReadOnlyList<string> SourceTags = new[] { "SubDL", "OpenSubtitles", "Jimaku" };

    /// <summary>Gets a value indicating whether SubtitleForge made the file.</summary>
    public bool IsOwn => Origin != "foreign";

    /// <summary>
    /// Classifies a subtitle file of a video.
    /// </summary>
    /// <param name="videoPath">Path of the video.</param>
    /// <param name="path">Path of the subtitle.</param>
    /// <param name="sourceTags">Download source names (defaults to <see cref="SourceTags"/>).</param>
    /// <returns>Origin.</returns>
    public static SubtitleOrigin Classify(string videoPath, string path, IEnumerable<string>? sourceTags = null)
    {
        var prefix = Path.GetFileNameWithoutExtension(videoPath) + ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.StartsWith(prefix, StringComparison.Ordinal))
        {
            return new SubtitleOrigin("foreign", null, string.Empty, false);
        }

        var rest = stem[prefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return new SubtitleOrigin("foreign", rest, string.Empty, false);
        }

        var language = rest[..dot];
        var tag = rest[(dot + 1)..];
        var forced = tag.StartsWith("forced.", StringComparison.Ordinal);
        var raw = forced ? tag[7..] : tag;
        var branded = raw.StartsWith(SubtitleFileWriter.Brand, StringComparison.Ordinal);
        var bare = SubtitleFileWriter.StripBrand(raw);
        // Only the exact tags SubtitleForge writes (now or in older versions) count: files of other tools with
        // similar names ("whisper", "opensubtitles", "Forced") stay foreign and are never renamed or deleted.
        string origin;
        if ((sourceTags ?? SourceTags).Any(x => string.Equals(x, bare, StringComparison.Ordinal)))
        {
            origin = "download";
        }
        else if (TranslationTagRegex().IsMatch(bare))
        {
            origin = "translation";
        }
        else if (WhisperTagRegex().IsMatch(bare))
        {
            origin = "whisper";
        }
        else if (OcrTagRegex().IsMatch(bare))
        {
            origin = "ocr";
        }
        else if (bare is "Sync" or "Text" || branded)
        {
            origin = "plugin";
        }
        else
        {
            origin = "foreign";
        }

        return new SubtitleOrigin(origin, language, (forced ? "forced." : string.Empty) + bare, branded);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\[AI\] (Übersetzt|Translated) [a-z]{2}-[a-z]{2}$")]
    private static partial System.Text.RegularExpressions.Regex TranslationTagRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\[AI\] Whisper( [a-z]{2}-[a-z]{2})?|Whisper(-[a-z]{2})?)$")]
    private static partial System.Text.RegularExpressions.Regex WhisperTagRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^OCR[0-9]{0,2}$")]
    private static partial System.Text.RegularExpressions.Regex OcrTagRegex();
}
