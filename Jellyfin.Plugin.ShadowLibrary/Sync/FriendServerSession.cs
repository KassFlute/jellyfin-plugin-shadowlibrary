namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// An authenticated session against a friend server.
/// </summary>
public class FriendServerSession
{
    /// <summary>
    /// Gets the normalised friend server URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the session token.
    /// </summary>
    public required string AccessToken { get; init; }

    /// <summary>
    /// Gets the service account identifier on the friend server.
    /// </summary>
    public required string RemoteUserId { get; init; }

    /// <summary>
    /// Gets the device id presented to the friend server.
    /// </summary>
    public required string DeviceId { get; init; }
}
