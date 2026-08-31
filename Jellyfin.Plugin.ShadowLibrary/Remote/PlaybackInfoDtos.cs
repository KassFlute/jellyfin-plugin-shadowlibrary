namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// Subset of the friend server answer to a playback info request.
/// </summary>
public class PlaybackInfoResponse
{
    /// <summary>
    /// Gets or sets the media sources the item can be played from.
    /// </summary>
    public RemoteMediaSource[] MediaSources { get; set; } = Array.Empty<RemoteMediaSource>();

    /// <summary>
    /// Gets or sets the play session the friend server opened for this request.
    /// </summary>
    public string? PlaySessionId { get; set; }
}

/// <summary>
/// One playable source of a remote item.
/// </summary>
public class RemoteMediaSource
{
    /// <summary>
    /// Gets or sets the media source identifier.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the friend server can serve this source as is.
    /// </summary>
    public bool SupportsDirectStream { get; set; }
}
