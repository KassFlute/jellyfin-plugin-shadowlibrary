using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.ShadowLibrary.Configuration;

namespace Jellyfin.Plugin.ShadowLibrary.Api;

/// <summary>
/// A friend server as exposed to the dashboard. Carries no password and no token,
/// not even encrypted.
/// </summary>
public class FriendServerDto
{
    /// <summary>
    /// Gets or sets the local identifier of the entry.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service account username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a password is stored for this server.
    /// </summary>
    public bool HasPassword { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this friend server is synchronised.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every visible library is synchronised.
    /// </summary>
    public bool SyncAllLibraries { get; set; }

    /// <summary>
    /// Gets or sets the selected libraries.
    /// </summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the Jellyfin movie library the generated folder is attached to.
    /// </summary>
    public string MovieLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin show library the generated folder is attached to.
    /// </summary>
    public string ShowLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the mode detected on the last successful contact.
    /// </summary>
    public FriendServerMode LastKnownMode { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful contact.
    /// </summary>
    public DateTime? LastContactUtc { get; set; }
}

/// <summary>
/// Body used to create or update a friend server.
/// </summary>
public class FriendServerRequest
{
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL.
    /// </summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service account username.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service account password. Leave empty on update to keep the stored one.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this friend server is synchronised.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every visible library is synchronised.
    /// </summary>
    public bool SyncAllLibraries { get; set; } = true;

    /// <summary>
    /// Gets or sets the selected libraries.
    /// </summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the Jellyfin movie library to attach the generated folder to.
    /// </summary>
    public string MovieLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin show library to attach the generated folder to.
    /// </summary>
    public string ShowLibraryName { get; set; } = string.Empty;
}

/// <summary>
/// Body used to test a connection.
/// </summary>
public class ConnectionTestRequest
{
    /// <summary>
    /// Gets or sets the identifier of an already saved friend server, so its stored password
    /// can be reused when <see cref="Password"/> is empty.
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// Gets or sets the base URL to test.
    /// </summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service account username.
    /// </summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service account password.
    /// </summary>
    public string? Password { get; set; }
}

/// <summary>
/// What the generated .strm files point at.
/// </summary>
public class StreamBaseUrlResult
{
    /// <summary>
    /// Gets or sets the address currently written into the .strm files.
    /// </summary>
    public string Effective { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where the address comes from: override, published, startup, request or local.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this call is what filled the setting in.
    /// </summary>
    public bool Adopted { get; set; }
}
