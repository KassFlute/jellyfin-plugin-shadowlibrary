using MediaBrowser.Controller.Entities;

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
}
