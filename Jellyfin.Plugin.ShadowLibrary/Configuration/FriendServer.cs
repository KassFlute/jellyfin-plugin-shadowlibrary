namespace Jellyfin.Plugin.ShadowLibrary.Configuration;

/// <summary>
/// A remote Jellyfin instance configured as a content source.
/// </summary>
public class FriendServer
{
    /// <summary>
    /// Gets or sets the local identifier of this entry.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the display name, also used in the origin tag applied to imported items.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL, without a trailing slash.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service account username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the encrypted service account password.
    /// </summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the encrypted session token obtained from the friend server.
    /// </summary>
    public string EncryptedAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service account identifier on the friend server.
    /// </summary>
    public string RemoteUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this friend server is synchronised.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every library visible to the service account
    /// is synchronised, rather than the <see cref="LibraryIds"/> selection.
    /// </summary>
    public bool SyncAllLibraries { get; set; } = true;

    /// <summary>
    /// Gets or sets the libraries to synchronise. Ignored when <see cref="SyncAllLibraries"/> is set.
    /// </summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the folder name used on disk for this friend server. Chosen once when the
    /// entry is created and never recomputed, so renaming the entry moves nothing.
    /// </summary>
    public string FolderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the folder the generated media actually sits in. Compared against the
    /// folder the current configuration asks for, to detect a media root change.
    /// </summary>
    public string GeneratedFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin movie library the generated Movies folder is attached to.
    /// Empty leaves it unattached.
    /// </summary>
    public string MovieLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Jellyfin show library the generated Shows folder is attached to.
    /// Empty leaves it unattached.
    /// </summary>
    public string ShowLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movie library the folder was actually attached to. Kept apart from
    /// the wanted value so a path the user removed by hand is not silently put back.
    /// </summary>
    public string AttachedMovieLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the show library the folder was actually attached to.
    /// </summary>
    public string AttachedShowLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movie folder path that was actually declared in the library. Kept so
    /// the old path can be detached when the generated folder moves.
    /// </summary>
    public string AttachedMoviePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the show folder path that was actually declared in the library.
    /// </summary>
    public string AttachedShowPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the mode detected on the last successful contact.
    /// </summary>
    public FriendServerMode LastKnownMode { get; set; } = FriendServerMode.Unknown;

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful contact.
    /// </summary>
    public DateTime? LastContactUtc { get; set; }

    /// <summary>
    /// Gets or sets the UTC timestamp of the last completed synchronisation cycle.
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }
}
