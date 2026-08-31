namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// Subset of <c>GET /System/Info/Public</c> on a friend server.
/// </summary>
public class PublicSystemInfo
{
    /// <summary>
    /// Gets or sets the friendly server name.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin version.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the server identifier.
    /// </summary>
    public string? Id { get; set; }
}

/// <summary>
/// Subset of <c>POST /Users/AuthenticateByName</c>.
/// </summary>
public class AuthenticationResult
{
    /// <summary>
    /// Gets or sets the session token.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the authenticated user.
    /// </summary>
    public AuthenticatedUser? User { get; set; }
}

/// <summary>
/// User returned by the authentication call.
/// </summary>
public class AuthenticatedUser
{
    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// A library visible to the service account on a friend server.
/// </summary>
public class RemoteLibrary
{
    /// <summary>
    /// Gets or sets the library identifier on the friend server.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin collection type, for example <c>movies</c> or <c>tvshows</c>.
    /// </summary>
    public string? CollectionType { get; set; }

    /// <summary>
    /// Gets a value indicating whether ShadowLibrary can import this library.
    /// Only movies and shows are in scope.
    /// </summary>
    public bool IsSupported =>
        string.Equals(CollectionType, "movies", StringComparison.OrdinalIgnoreCase)
        || string.Equals(CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Paged result envelope used by the Jellyfin API.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public class QueryResult<T>
{
    /// <summary>
    /// Gets or sets the returned items.
    /// </summary>
    public T[] Items { get; set; } = Array.Empty<T>();

    /// <summary>
    /// Gets or sets the total number of items matching the query.
    /// </summary>
    public int TotalRecordCount { get; set; }
}

/// <summary>
/// Response of <c>/ShadowLibrary/ping</c> on a friend server.
/// </summary>
public class FriendPingResponse
{
    /// <summary>
    /// Gets or sets the plugin name.
    /// </summary>
    public string? Plugin { get; set; }

    /// <summary>
    /// Gets or sets the plugin version.
    /// </summary>
    public string? Version { get; set; }
}
