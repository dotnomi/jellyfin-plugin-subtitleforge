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
        libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Video>().Where(v => !string.IsNullOrEmpty(v.Path) && File.Exists(v.Path)).ToList();
}
