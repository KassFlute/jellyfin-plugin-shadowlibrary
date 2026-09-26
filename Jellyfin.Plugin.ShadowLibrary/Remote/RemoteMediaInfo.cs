using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// What the friend server learned by inspecting the file of an item, in the models Jellyfin
/// itself stores, so it can be written locally without inspecting the file again.
/// </summary>
public class RemoteMediaInfo
{
    /// <summary>
    /// Gets or sets the item identifier on the friend server.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media sources, each carrying its streams and attachments.
    /// </summary>
    public MediaSourceInfo[]? MediaSources { get; set; }

    /// <summary>
    /// Gets or sets the chapters.
    /// </summary>
    public ChapterInfo[]? Chapters { get; set; }
}
