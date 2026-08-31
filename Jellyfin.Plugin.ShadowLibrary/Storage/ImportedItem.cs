namespace Jellyfin.Plugin.ShadowLibrary.Storage;

/// <summary>
/// What kind of media an imported row stands for.
/// </summary>
public enum ImportedItemKind
{
    /// <summary>
    /// A movie, which owns the folder holding its files.
    /// </summary>
    Movie = 0,

    /// <summary>
    /// An episode, which shares its season folder with its siblings.
    /// </summary>
    Episode = 1
}

/// <summary>
/// One playable item imported from a friend server. This table, not the Jellyfin tags,
/// decides what the plugin considers imported.
/// </summary>
public class ImportedItem
{
    /// <summary>
    /// Gets or sets the plugin side identifier, used in the generated .strm URL.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the friend server this item comes from.
    /// </summary>
    public Guid FriendServerId { get; set; }

    /// <summary>
    /// Gets or sets the item identifier on the friend server.
    /// </summary>
    public string RemoteItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what this row stands for.
    /// </summary>
    public ImportedItemKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin item identifier on this server, resolved from the .strm
    /// path once the library scan has picked the file up.
    /// </summary>
    public Guid? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the folder holding the generated files. A movie owns it, an episode
    /// shares its season folder with the other episodes.
    /// </summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path of the generated .strm file.
    /// </summary>
    public string StrmPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path of the generated .nfo file.
    /// </summary>
    public string NfoPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful import or update.
    /// </summary>
    public DateTime LastImportUtc { get; set; }

    /// <summary>
    /// Gets or sets when the item started being continuously unavailable, null while it
    /// is available. Drives the deferred removal threshold.
    /// </summary>
    public DateTime? UnavailableSinceUtc { get; set; }

    /// <summary>
    /// Gets or sets the hash of the remote metadata, so an unchanged item does not get
    /// its .nfo rewritten.
    /// </summary>
    public string MetadataHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the deduplication keys this item occupies, so a second friend server
    /// holding the same media does not import it a second time.
    /// </summary>
    public string[] ClaimKeys { get; set; } = Array.Empty<string>();
}

/// <summary>
/// A series folder generated for a friend server. It holds no playable file of its own,
/// only the show metadata shared by its episodes.
/// </summary>
public class ImportedSeries
{
    /// <summary>
    /// Gets or sets the friend server this series comes from.
    /// </summary>
    public Guid FriendServerId { get; set; }

    /// <summary>
    /// Gets or sets the series identifier on the friend server.
    /// </summary>
    public string RemoteSeriesId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generated series folder.
    /// </summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash of the remote series metadata.
    /// </summary>
    public string MetadataHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp of the last successful import or update.
    /// </summary>
    public DateTime LastImportUtc { get; set; }
}
