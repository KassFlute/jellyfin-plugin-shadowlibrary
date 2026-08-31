namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// A series as returned by the friend server.
/// </summary>
public class RemoteSeries
{
    /// <summary>
    /// Gets or sets the item identifier on the friend server.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the series title.
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
    /// Gets or sets the first air year.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the first air date.
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
    /// Gets or sets the production status, Continuing or Ended.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[]? Genres { get; set; }

    /// <summary>
    /// Gets or sets the studios.
    /// </summary>
    public NamedEntity[]? Studios { get; set; }

    /// <summary>
    /// Gets or sets the cast and crew.
    /// </summary>
    public RemotePerson[]? People { get; set; }

    /// <summary>
    /// Gets or sets the external identifiers.
    /// </summary>
    public Dictionary<string, string>? ProviderIds { get; set; }

    /// <summary>
    /// Gets or sets the available image tags.
    /// </summary>
    public Dictionary<string, string>? ImageTags { get; set; }

    /// <summary>
    /// Gets or sets the backdrop image tags.
    /// </summary>
    public string[]? BackdropImageTags { get; set; }
}

/// <summary>
/// An episode as returned by the friend server.
/// </summary>
public class RemoteEpisode
{
    /// <summary>
    /// Gets or sets the item identifier on the friend server.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the identifier of the series this episode belongs to.
    /// </summary>
    public string? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number within its season.
    /// </summary>
    public int? IndexNumber { get; set; }

    /// <summary>
    /// Gets or sets the air date.
    /// </summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>
    /// Gets or sets the synopsis.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the runtime in ticks.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the cast and crew.
    /// </summary>
    public RemotePerson[]? People { get; set; }

    /// <summary>
    /// Gets or sets the external identifiers.
    /// </summary>
    public Dictionary<string, string>? ProviderIds { get; set; }

    /// <summary>
    /// Gets or sets the available image tags.
    /// </summary>
    public Dictionary<string, string>? ImageTags { get; set; }

    /// <summary>
    /// Gets a value indicating whether the episode can be placed in a season folder.
    /// Episodes with no numbering are skipped, Jellyfin could not file them either.
    /// </summary>
    public bool IsPlaceable =>
        !string.IsNullOrEmpty(SeriesId) && ParentIndexNumber is not null && IndexNumber is not null;
}
