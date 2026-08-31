namespace Jellyfin.Plugin.ShadowLibrary.Configuration;

/// <summary>
/// How the plugin deals with a given friend server.
/// </summary>
public enum FriendServerMode
{
    /// <summary>
    /// The friend server has not been contacted yet.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The friend server does not run ShadowLibrary, so every item it exposes is a candidate.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// The friend server runs ShadowLibrary, so only its native items are imported.
    /// </summary>
    Federated = 2
}
