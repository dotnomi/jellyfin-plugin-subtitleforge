using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubtitleForge.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Which subtitle languages an item should have. Most specific setting wins:
/// tag on the item ("subs:de,ja" / "subs:none") → tag on its season/series → the library's own
/// "subtitle download languages" → the plugin default.
/// </summary>
public class LanguageResolver
{
    /// <summary>Tag prefix.</summary>
    public const string TagPrefix = "subs:";

    /// <summary>Tag prefix for the automatic Whisper switch ("whisper:auto" / "whisper:off").</summary>
    public const string WhisperTagPrefix = "whisper:";

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageResolver"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public LanguageResolver(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Parses a tag value like "subs:de, ja" into ISO 639-1 codes (null if the tag is not a subs tag).
    /// </summary>
    /// <param name="tag">Tag.</param>
    /// <returns>Languages; empty for "subs:none".</returns>
    public static List<string>? ParseTag(string tag)
    {
        if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = tag[TagPrefix.Length..].Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("keine", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }

        return ParseList(value);
    }

    /// <summary>
    /// Parses a Whisper tag: "whisper:auto" (also on/an/ja) → true, "whisper:off" (also aus/nein/manual) → false.
    /// </summary>
    /// <param name="tag">Tag.</param>
    /// <returns>Switch, or null if not a Whisper tag.</returns>
    public static bool? ParseWhisperTag(string tag)
    {
        if (!tag.StartsWith(WhisperTagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return tag[WhisperTagPrefix.Length..].Trim().ToLowerInvariant() switch
        {
            "auto" or "on" or "an" or "ja" or "yes" => true,
            "off" or "aus" or "nein" or "no" or "manual" or "manuell" => false,
            _ => null,
        };
    }

    /// <summary>
    /// Whether Whisper runs automatically for an item: tag on the item/season/series wins, otherwise the plugin setting.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <param name="source">Where the decision came from.</param>
    /// <returns>True if automatic.</returns>
    public static bool ResolveWhisperAuto(BaseItem item, out string source)
    {
        foreach (var candidate in Chain(item))
        {
            foreach (var tag in candidate.Tags ?? Array.Empty<string>())
            {
                if (ParseWhisperTag(tag) is { } value)
                {
                    source = Loc.T("tag on ", "Tag an ") + candidate.Name;
                    return value;
                }
            }
        }

        source = Loc.T("plugin setting", "Plugin-Einstellung");
        return (Plugin.Instance?.Configuration ?? new PluginConfiguration()).WhisperAutoFallback;
    }

    /// <summary>
    /// Parses "de,eng, ja" into distinct ISO 639-1 codes.
    /// </summary>
    /// <param name="value">List.</param>
    /// <returns>Codes.</returns>
    public static List<string> ParseList(string? value) =>
        (value ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Lang.ToTwoLetter).Where(l => l is not null).Cast<string>().Distinct().ToList();

    /// <summary>
    /// Resolves the wanted languages for an item.
    /// </summary>
    /// <param name="item">Item.</param>
    /// <param name="source">Where the setting came from (for the report).</param>
    /// <returns>ISO 639-1 codes (empty = nothing wanted).</returns>
    public List<string> Resolve(BaseItem item, out string source)
    {
        foreach (var candidate in Chain(item))
        {
            foreach (var tag in candidate.Tags ?? Array.Empty<string>())
            {
                var parsed = ParseTag(tag);
                if (parsed is not null)
                {
                    source = Loc.T("tag on ", "Tag an ") + candidate.Name;
                    return parsed;
                }
            }
        }

        try
        {
            var libraryLanguages = _libraryManager.GetLibraryOptions(item)?.SubtitleDownloadLanguages;
            if (libraryLanguages is { Length: > 0 })
            {
                source = Loc.T("library", "Bibliothek");
                return ParseList(string.Join(',', libraryLanguages));
            }
        }
        catch (InvalidOperationException)
        {
            // item without library (e.g. during removal)
        }

        source = Loc.T("plugin default", "Plugin-Standard");
        return ParseList((Plugin.Instance?.Configuration ?? new PluginConfiguration()).DownloadLanguages);
    }

    private static IEnumerable<BaseItem> Chain(BaseItem item)
    {
        yield return item;
        if (item is Episode episode)
        {
            if (episode.Season is { } season)
            {
                yield return season;
            }

            if (episode.Series is { } series)
            {
                yield return series;
            }
        }
    }
}
