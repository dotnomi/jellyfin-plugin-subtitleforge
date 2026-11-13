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
    /// <returns>Cues, or null if nothing usable.</returns>
    public static List<SubtitleCue>? Parse(byte[] payload, string? fileNameHint, int? season, int? episode)
    {
        var files = new List<(string Name, byte[] Data)>();
        if (payload.Length > 4 && payload[0] == 'P' && payload[1] == 'K')
        {
            using var zip = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                if (TextExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase) && entry.Length < 20_000_000)
                {
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    files.Add((entry.FullName, ms.ToArray()));
                }
            }
        }
        else
        {
            files.Add((fileNameHint ?? "subtitle.srt", payload));
        }

        if (files.Count == 0)
        {
            return null;
        }

        var chosen = files
            .OrderByDescending(f => episode.HasValue && MatchesEpisode(f.Name, season, episode.Value) ? 1 : 0)
            .ThenByDescending(f => f.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(f => f.Data.Length)
            .First();

        if (files.Count > 1 && episode.HasValue && !MatchesEpisode(chosen.Name, season, episode.Value))
        {
            return null; // a pack without the wanted episode
        }

        var text = Decode(chosen.Data);
        var cues = chosen.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) || text.Contains("-->", StringComparison.Ordinal)
            ? Srt.Parse(text)
            : ParseAss(text);
        cues = SubtitleCleaner.Clean(cues);
        return cues.Count > 0 ? cues : null;
    }

    /// <summary>
    /// Decodes subtitle bytes: BOM, strict UTF-8, otherwise Windows-1252 (typical for German/English subs).
    /// </summary>
    /// <param name="data">Bytes.</param>
    /// <returns>Text.</returns>
    public static string Decode(byte[] data)
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
            return Encoding.GetEncoding(1252).GetString(data);
        }
    }

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

            cues.Add(new SubtitleCue(ParseAssTime(parts[start]), ParseAssTime(parts[end]), body));
        }

        return cues;
    }

    private static bool MatchesEpisode(string name, int? season, int episode)
    {
        var n = Path.GetFileNameWithoutExtension(name);
        var se = SeasonEpisodeRegex().Match(n);
        if (se.Success)
        {
            var s = int.Parse(se.Groups[1].Value, CultureInfo.InvariantCulture);
            var e = int.Parse(se.Groups[2].Value, CultureInfo.InvariantCulture);
            return e == episode && (!season.HasValue || s == season.Value);
        }

        var abs = AnimeEpisodeRegex().Match(n);
        return abs.Success && int.Parse(abs.Groups[1].Value, CultureInfo.InvariantCulture) == episode;
    }

    private static long ParseAssTime(string s)
    {
        // h:mm:ss.cc
        var p = s.Trim().Split(':', '.');
        return (long.Parse(p[0], CultureInfo.InvariantCulture) * 3600000)
            + (long.Parse(p[1], CultureInfo.InvariantCulture) * 60000)
            + (long.Parse(p[2], CultureInfo.InvariantCulture) * 1000)
            + (long.Parse(p[3].PadRight(3, '0')[..3], CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\{[^}]*\\p[1-9]")]
    private static partial Regex DrawingRegex();

    [GeneratedRegex(@"[Ss](\d{1,2})[ ._-]*[Ee](\d{1,3})")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?:^|[\s_\-\.])(?:E|EP|Ep|Folge\s*)?(\d{1,3})(?:v\d)?(?=[\s_\.\[\(]|$)")]
    private static partial Regex AnimeEpisodeRegex();
}
