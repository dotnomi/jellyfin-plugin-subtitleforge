using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SubtitleForge.Common;

namespace Jellyfin.Plugin.SubtitleForge.Providers;

/// <summary>
/// Turns downloaded payloads (zip archives, SRT/ASS in any common encoding) into cues.
/// </summary>
public static partial class SubtitleContent
{
    private static readonly string[] TextExtensions = { ".srt", ".ass", ".ssa" };

    static SubtitleContent()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Extracts the best subtitle file from a payload and parses it.
    /// </summary>
    /// <param name="payload">Downloaded bytes.</param>
    /// <param name="fileNameHint">File name of the payload, if known.</param>
    /// <param name="season">Wanted season (to pick from packs).</param>
    /// <param name="episode">Wanted episode (to pick from packs).</param>
    /// <param name="language">Language of the subtitle, if known (picks the legacy code page for files that are not UTF-8).</param>
    /// <returns>Cues, or null if nothing usable.</returns>
    public static List<SubtitleCue>? Parse(byte[] payload, string? fileNameHint, int? season, int? episode, string? language = null)
    {
        // Archives come from third parties: only entry names and declared sizes are looked at first, then just the
        // chosen entry is decompressed, with a hard size limit (protects against zip bombs).
        const int MaxEntries = 1000;
        const long MaxSubtitleBytes = 8L * 1024 * 1024;
        (string Name, byte[] Data) chosen;
        var count = 1;
        if (payload.Length > 4 && payload[0] == 'P' && payload[1] == 'K')
        {
            using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
            var entries = zip.Entries.Take(MaxEntries)
                .Where(e => TextExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase) && e.Length > 0 && e.Length <= MaxSubtitleBytes)
                .ToList();
            if (entries.Count == 0)
            {
                return null;
            }

            var entry = entries
                .OrderByDescending(e => episode.HasValue && MatchesEpisode(e.FullName, season, episode.Value) ? 1 : 0)
                .ThenByDescending(e => e.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenByDescending(e => e.Length)
                .First();
            using var stream = entry.Open();
            var data = Jellyfin.Plugin.SubtitleForge.Common.SafeHttp.ReadLimited(stream, MaxSubtitleBytes);
            if (data is null)
            {
                return null;
            }

            chosen = (entry.FullName, data);
            count = entries.Count;
        }
        else
        {
            if (payload.Length > MaxSubtitleBytes)
            {
                return null;
            }

            chosen = (fileNameHint ?? "subtitle.srt", payload);
        }

        if (count > 1 && episode.HasValue && !MatchesEpisode(chosen.Name, season, episode.Value))
        {
            return null; // a pack without the wanted episode
        }

        var text = Decode(chosen.Data, language);
        var cues = chosen.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || text.Contains("-->", StringComparison.Ordinal)
            ? Srt.Parse(text)
            : ParseAss(text);
        cues = SubtitleCleaner.Clean(cues);
        return cues.Count > 0 ? cues : null;
    }

    /// <summary>
    /// Decodes subtitle bytes: BOM, strict UTF-8, otherwise the usual legacy code page of the language
    /// (Windows-1252 for German/English and unknown languages, 1251 for Cyrillic, 1250 for Central Europe, Shift-JIS …).
    /// </summary>
    /// <param name="data">Bytes.</param>
    /// <param name="language">Language, if known.</param>
    /// <returns>Text.</returns>
    public static string Decode(byte[] data, string? language = null)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(LegacyCodePage(language)).GetString(data);
        }
    }

    /// <summary>
    /// The Windows code page old subtitle files of a language are usually written in.
    /// </summary>
    /// <param name="language">Language code.</param>
    /// <returns>Code page.</returns>
    internal static int LegacyCodePage(string? language) => Common.Lang.ToTwoLetter(language) switch
    {
        "ru" or "uk" or "bg" or "sr" or "mk" or "be" => 1251,
        "pl" or "cs" or "sk" or "hu" or "hr" or "sl" or "ro" or "bs" => 1250,
        "el" => 1253,
        "tr" => 1254,
        "he" => 1255,
        "ar" or "fa" => 1256,
        "et" or "lv" or "lt" => 1257,
        "vi" => 1258,
        "th" => 874,
        "ja" => 932,
        "zh" => 936,
        "ko" => 949,
        _ => 1252,
    };

    /// <summary>
    /// Parses ASS/SSA dialogue events. Vector drawings are dropped.
    /// </summary>
    /// <param name="text">Script.</param>
    /// <returns>Cues.</returns>
    public static List<SubtitleCue> ParseAss(string text)
    {
        var cues = new List<SubtitleCue>();
        string[]? format = null;
        var inEvents = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > Srt.MaxCueLength + 500)
            {
                continue; // untrusted input: absurd lines are dropped before any regex runs
            }

            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                format = line[7..].Split(',').Select(s => s.Trim()).ToArray();
                continue;
            }

            if (format is null || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line[9..].Split(',', format.Length);
            if (parts.Length < format.Length)
            {
                continue;
            }

            var start = Array.FindIndex(format, f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
            var end = Array.FindIndex(format, f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
            var body = parts[^1];
            if (start < 0 || end < 0 || DrawingRegex().IsMatch(body))
            {
                continue;
            }

            var startMs = ParseAssTime(parts[start]);
            var endMs = ParseAssTime(parts[end]);
            if (startMs < 0 || endMs < 0)
            {
                continue;
            }

            cues.Add(new SubtitleCue(startMs, endMs, body));
        }

        return cues;
    }

    private static bool MatchesEpisode(string name, int? season, int episode)
    {
        var n = Path.GetFileNameWithoutExtension(name);
        var se = SeasonEpisodeRegex().Match(n);
        if (se.Success)
        {
            return int.TryParse(se.Groups[1].Value, CultureInfo.InvariantCulture, out var s)
                && int.TryParse(se.Groups[2].Value, CultureInfo.InvariantCulture, out var e)
                && e == episode && (!season.HasValue || s == season.Value);
        }

        var abs = AnimeEpisodeRegex().Match(n);
        return abs.Success && int.TryParse(abs.Groups[1].Value, CultureInfo.InvariantCulture, out var a) && a == episode;
    }

    private static long ParseAssTime(string s)
    {
        // h:mm:ss.cc
        var p = s.Trim().Split(':', '.');
        if (p.Length < 4
            || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h > 99
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m > 59
            || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sec) || sec > 59
            || !int.TryParse(p[3].PadRight(3, '0')[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
        {
            return -1;
        }

        return (h * 3600000L) + (m * 60000L) + (sec * 1000L) + ms;
    }

    [GeneratedRegex(@"\{[^}]*\\p[1-9]")]
    private static partial Regex DrawingRegex();

    [GeneratedRegex(@"[Ss](\d{1,2})[ ._-]*[Ee](\d{1,3})")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?:^|[\s_\-\.])(?:E|EP|Ep|Folge\s*)?(\d{1,3})(?:v\d)?(?=[\s_\.\[\(]|$)")]
    private static partial Regex AnimeEpisodeRegex();
}
