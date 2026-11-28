using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Recognises the Jellyfin items that come from a generated .strm.
/// </summary>
public static class ImportedItems
{
    /// <summary>
    /// Part of the URL every generated .strm holds.
    /// </summary>
    public const string RelayPathMarker = "/ShadowLibrary/stream/";

    /// <summary>
    /// Tells whether an item plays through the relay, whatever path it was scanned from.
    /// </summary>
    /// <param name="item">Item to look at.</param>
    /// <returns>True for an imported item.</returns>
    /// <remarks>
    /// The same files can be declared under several paths, a second mount of the media for
    /// instance, so neither the path nor the stored item identifier is enough.
    /// </remarks>
    public static bool IsImported(BaseItem? item)
        => item is Video { IsShortcut: true } video
            && video.ShortcutPath?.Contains(RelayPathMarker, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Reads the plugin side identifier out of the relay URL of an imported item.
    /// </summary>
    /// <param name="video">Imported item.</param>
    /// <returns>The identifier, as written in the URL.</returns>
    public static string KeyOf(Video video)
    {
        var path = (video.ShortcutPath ?? string.Empty).Trim().Split('?')[0];
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Lists the imported items of the local libraries by plugin side identifier. One import
    /// gives several items when its folder is declared under several mounts.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <returns>The items.</returns>
    public static ILookup<Guid, Video> Find(ILibraryManager libraryManager)
        => libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true
        })
            .Where(IsImported)
            .Cast<Video>()
            .ToLookup(v => Guid.TryParse(KeyOf(v), CultureInfo.InvariantCulture, out var id) ? id : Guid.Empty);
}
