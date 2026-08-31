using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ShadowLibrary.Configuration;

/// <summary>
/// Persisted ShadowLibrary settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets how long a friend server item may stay unreachable, in hours,
    /// before it is removed from the local library.
    /// </summary>
    public int UnavailabilityThresholdHours { get; set; } = 48;

    /// <summary>
    /// Gets or sets the root folder where generated .strm files are written.
    /// </summary>
    public string MediaRootPath { get; set; } = "/media/shadowlibrary";

    /// <summary>
    /// Gets or sets the address the generated .strm files point at. Empty means the address
    /// is worked out from the server, see MediaFileWriter.ResolveBaseUrl.
    /// </summary>
    public string LocalApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the address was already filled in from an
    /// administrator connection. Set once, so clearing the field afterwards is respected
    /// rather than undone on the next visit to the settings page.
    /// </summary>
    public bool StreamBaseUrlDetected { get; set; }

    /// <summary>
    /// Gets or sets the key the .strm URLs carry, so the stream endpoint can accept the
    /// media pipeline without a user session. Generated on first use.
    /// </summary>
    public string StreamAccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the configured friend servers.
    /// </summary>
    public FriendServer[] FriendServers { get; set; } = Array.Empty<FriendServer>();
}
