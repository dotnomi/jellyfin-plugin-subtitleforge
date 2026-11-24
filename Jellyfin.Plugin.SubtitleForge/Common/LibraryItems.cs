using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SubtitleForge.Common;

/// <summary>
/// Library queries.
/// </summary>
public static class LibraryItems
{
    /// <summary>
    /// All movies and episodes with an existing file.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <returns>Videos.</returns>
    public static List<Video> Videos(ILibraryManager libraryManager) =>
        AllVideos(libraryManager).Where(v => !string.IsNullOrEmpty(v.Path) && File.Exists(v.Path)).ToList();

    /// <summary>
    /// All movies and episodes in the enabled libraries (file existence not checked).
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <returns>Videos.</returns>
    public static List<Video> AllVideos(ILibraryManager libraryManager)
    {
        var enabled = EnabledLibraries();
        return libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Video>().Where(v => InLibraries(libraryManager, v, enabled)).ToList();
    }

    /// <summary>
    /// Whether the item lies in one of the libraries the plugin is enabled for.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="item">Item.</param>
    /// <returns>True if enabled (or no restriction is configured).</returns>
    public static bool IsEnabled(ILibraryManager libraryManager, BaseItem item) =>
        InLibraries(libraryManager, item, EnabledLibraries());

    /// <summary>
    /// The video itself, or all movies/episodes below a folder (season, series, collection, library).
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="item">Item.</param>
    /// <returns>Videos ordered by season/episode/name.</returns>
    public static List<Video> VideosUnder(ILibraryManager libraryManager, BaseItem item)
    {
        if (item is Video v)
        {
            return new List<Video> { v };
        }

        return libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = new[] { item.Id },
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Video>()
            .Where(x => !string.IsNullOrEmpty(x.Path))
            .OrderBy(x => x.ParentIndexNumber ?? 0).ThenBy(x => x.IndexNumber ?? 0).ThenBy(x => x.SortName, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<Guid>? EnabledLibraries()
    {
        var ids = Plugin.Instance?.Configuration.Libraries;
        if (ids is null || ids.Length == 0)
        {
            return null;
        }

        return ids.Select(i => Guid.TryParse(i, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
    }

    private static bool InLibraries(ILibraryManager libraryManager, BaseItem item, HashSet<Guid>? enabled) =>
        enabled is null || libraryManager.GetCollectionFolders(item).Any(f => enabled.Contains(f.Id));
}
