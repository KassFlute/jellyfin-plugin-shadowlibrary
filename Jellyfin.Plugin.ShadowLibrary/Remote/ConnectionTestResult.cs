using Jellyfin.Plugin.ShadowLibrary.Configuration;

namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// Outcome of a friend server connection test.
/// </summary>
public class ConnectionTestResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the friend server was reached and the
    /// service account authenticated.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a message describing the outcome, shown as is in the dashboard.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the friend server name.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin version of the friend server.
    /// </summary>
    public string? ServerVersion { get; set; }

    /// <summary>
    /// Gets or sets the detected mode.
    /// </summary>
    public FriendServerMode Mode { get; set; } = FriendServerMode.Unknown;

    /// <summary>
    /// Gets or sets the ShadowLibrary version running on the friend server, in federated mode.
    /// </summary>
    public string? RemotePluginVersion { get; set; }

    /// <summary>
    /// Gets or sets the libraries visible to the service account.
    /// </summary>
    public RemoteLibrary[] Libraries { get; set; } = Array.Empty<RemoteLibrary>();
}
