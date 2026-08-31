namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// A movie as returned by the friend server, limited to what the .nfo needs.
/// </summary>
public class RemoteMovie
{
    /// <summary>
    /// Gets or sets the item identifier on the friend server.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sort title.
    /// </summary>
    public string? SortName { get; set; }

    /// <summary>
    /// Gets or sets the original title.
    /// </summary>
    public string? OriginalTitle { get; set; }

    /// <summary>
    /// Gets or sets the release year.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the release date.
    /// </summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>
    /// Gets or sets the synopsis.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the content rating.
    /// </summary>
    public string? OfficialRating { get; set; }

    /// <summary>
    /// Gets or sets the community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the runtime in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[]? Genres { get; set; }

    /// <summary>
    /// Gets or sets the taglines.
    /// </summary>
    public string[]? Taglines { get; set; }

    /// <summary>
    /// Gets or sets the studios.
    /// </summary>
    public NamedEntity[]? Studios { get; set; }

    /// <summary>
    /// Gets or sets the cast and crew.
    /// </summary>
    public RemotePerson[]? People { get; set; }

    /// <summary>
    /// Gets or sets the external identifiers, keyed by provider name such as Tmdb or Imdb.
    /// </summary>
    public Dictionary<string, string>? ProviderIds { get; set; }

    /// <summary>
    /// Gets or sets the available image tags, keyed by image type such as Primary or Logo.
    /// </summary>
    public Dictionary<string, string>? ImageTags { get; set; }

    /// <summary>
    /// Gets or sets the backdrop image tags.
    /// </summary>
    public string[]? BackdropImageTags { get; set; }
}

/// <summary>
/// A named entity returned by the Jellyfin API, such as a studio.
/// </summary>
public class NamedEntity
{
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// A cast or crew member.
/// </summary>
public class RemotePerson
{
    /// <summary>
    /// Gets or sets the person name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the character played, for actors.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Gets or sets the person type, such as Actor or Director.
    /// </summary>
    public string? Type { get; set; }
}

/// <summary>
/// Response of the native items endpoint.
/// </summary>
public class NativeItemsResponse
{
    /// <summary>
    /// Gets or sets the native item identifiers of this page.
    /// </summary>
    public string[] Items { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the total number of native items.
    /// </summary>
    public int TotalRecordCount { get; set; }

    /// <summary>
    /// Gets or sets the index this page starts at.
    /// </summary>
    public int StartIndex { get; set; }
}
