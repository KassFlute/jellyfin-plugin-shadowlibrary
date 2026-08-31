using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// What the local server already holds natively, keyed by external identifier, so a media
/// the user owns is never imported from a friend server. It also tracks what each friend
/// server has claimed during the run, so two friends holding the same film yield one item
/// rather than two.
/// </summary>
/// <remarks>
/// Movies and series are read up front, both are small. Episodes are read one series at a
/// time, and only for the series a friend server also has, since a library can hold tens of
/// thousands of episodes of which only the overlapping ones ever get compared.
/// </remarks>
public class LocalCatalogue
{
    private static readonly string[] Providers = ["Tmdb", "Imdb"];

    private readonly HashSet<string> _movieKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _claims = new(StringComparer.Ordinal);
    private readonly HashSet<string> _claimedThisRun = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Guid>> _seriesByKey = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _loadedSeries = [];
    private readonly HashSet<string> _episodeKeys = new(StringComparer.Ordinal);

    private readonly ILibraryManager _libraryManager;
    private readonly string _mediaRoot;

    private LocalCatalogue(ILibraryManager libraryManager, string mediaRootPath)
    {
        _libraryManager = libraryManager;
        _mediaRoot = Path.TrimEndingDirectorySeparator(mediaRootPath ?? string.Empty);
    }

    /// <summary>
    /// Reads the local movies and series once, leaving out everything ShadowLibrary generated.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="mediaRootPath">Root folder holding the generated media.</param>
    /// <returns>The catalogue.</returns>
    public static LocalCatalogue Build(ILibraryManager libraryManager, string mediaRootPath)
    {
        var catalogue = new LocalCatalogue(libraryManager, mediaRootPath);

        foreach (var movie in libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true
        }))
        {
            if (!catalogue.IsOurs(movie))
            {
                catalogue._movieKeys.UnionWith(KeysOf(movie.ProviderIds));
            }
        }

        foreach (var series in libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Series],
            Recursive = true
        }))
        {
            if (catalogue.IsOurs(series))
            {
                continue;
            }

            foreach (var key in KeysOf(series.ProviderIds))
            {
                if (!catalogue._seriesByKey.TryGetValue(key, out var ids))
                {
                    ids = [];
                    catalogue._seriesByKey[key] = ids;
                }

                ids.Add(series.Id);
            }
        }

        return catalogue;
    }

    /// <summary>
    /// Gives back the ownership recorded on a previous run, so a friend server that cannot be
    /// reached this time does not lose its items to another one that holds the same media.
    /// </summary>
    /// <param name="claims">Stored claims, from the imported item store.</param>
    public void SeedClaims(IEnumerable<(Guid FriendServerId, string[] Keys)> claims)
    {
        foreach (var (friendServerId, keys) in claims)
        {
            foreach (var key in keys)
            {
                _claims.TryAdd(key, friendServerId);
            }
        }
    }

    /// <summary>
    /// Claims a movie for a friend server, unless another one already holds it in this run.
    /// </summary>
    /// <param name="providerIds">External identifiers of the remote movie.</param>
    /// <param name="friendServerId">Friend server asking for it.</param>
    /// <param name="keys">The keys that were claimed, empty when the claim was refused.</param>
    /// <returns>True when the caller may import it.</returns>
    public bool TryClaimMovie(Dictionary<string, string>? providerIds, Guid friendServerId, out string[] keys)
        => TryClaim(KeysOf(providerIds), friendServerId, out keys);

    /// <summary>
    /// Claims an episode for a friend server, unless another one already holds it in this run.
    /// </summary>
    /// <param name="seriesProviderIds">External identifiers of the remote series.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="friendServerId">Friend server asking for it.</param>
    /// <param name="keys">The keys that were claimed, empty when the claim was refused.</param>
    /// <returns>True when the caller may import it.</returns>
    public bool TryClaimEpisode(
        Dictionary<string, string>? seriesProviderIds,
        int seasonNumber,
        int episodeNumber,
        Guid friendServerId,
        out string[] keys)
        => TryClaim(
            KeysOf(seriesProviderIds).Select(key => EpisodeKey(key, seasonNumber, episodeNumber)),
            friendServerId,
            out keys);

    private bool TryClaim(IEnumerable<string> candidates, Guid friendServerId, out string[] keys)
    {
        keys = candidates.ToArray();
        if (keys.Length == 0)
        {
            return false;
        }

        foreach (var key in keys)
        {
            // taken earlier in this run, by another friend server or by a second copy of the
            // same media on this one
            if (_claimedThisRun.Contains(key))
            {
                keys = [];
                return false;
            }

            // owned by another friend server on a previous run, which keeps ownership stable
            // when that server happens to be unreachable today
            if (_claims.TryGetValue(key, out var owner) && owner != friendServerId)
            {
                keys = [];
                return false;
            }
        }

        foreach (var key in keys)
        {
            _claims[key] = friendServerId;
            _claimedThisRun.Add(key);
        }

        return true;
    }

    /// <summary>
    /// Tells whether an item carries an identifier ShadowLibrary can match on.
    /// </summary>
    /// <param name="providerIds">External identifiers of the remote item.</param>
    /// <returns>True when at least one usable identifier is present.</returns>
    public static bool HasExternalId(Dictionary<string, string>? providerIds)
        => KeysOf(providerIds).Any();

    /// <summary>
    /// Tells whether the local server already holds this movie.
    /// </summary>
    /// <param name="providerIds">External identifiers of the remote movie.</param>
    /// <returns>True when a native local copy exists.</returns>
    public bool HasMovie(Dictionary<string, string>? providerIds)
        => KeysOf(providerIds).Any(_movieKeys.Contains);

    /// <summary>
    /// Tells whether the local server already holds this episode.
    /// </summary>
    /// <param name="seriesProviderIds">External identifiers of the remote series.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <returns>True when a native local copy exists.</returns>
    public bool HasEpisode(Dictionary<string, string>? seriesProviderIds, int seasonNumber, int episodeNumber)
    {
        var keys = KeysOf(seriesProviderIds).ToArray();
        if (keys.Length == 0)
        {
            return false;
        }

        LoadEpisodesOf(keys);
        return keys.Any(key => _episodeKeys.Contains(EpisodeKey(key, seasonNumber, episodeNumber)));
    }

    private void LoadEpisodesOf(string[] seriesKeys)
    {
        foreach (var key in seriesKeys)
        {
            if (!_seriesByKey.TryGetValue(key, out var seriesIds))
            {
                continue;
            }

            foreach (var seriesId in seriesIds)
            {
                if (!_loadedSeries.Add(seriesId))
                {
                    continue;
                }

                foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    AncestorIds = [seriesId]
                }))
                {
                    if (item is not Episode episode
                        || IsOurs(episode)
                        || episode.ParentIndexNumber is null
                        || episode.IndexNumber is null)
                    {
                        continue;
                    }

                    _episodeKeys.Add(
                        EpisodeKey(key, episode.ParentIndexNumber.Value, episode.IndexNumber.Value));
                }
            }
        }
    }

    private bool IsOurs(BaseItem item)
        => _mediaRoot.Length > 0
            && !string.IsNullOrEmpty(item.Path)
            && item.Path.StartsWith(_mediaRoot, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> KeysOf(Dictionary<string, string>? providerIds)
    {
        if (providerIds is null)
        {
            yield break;
        }

        foreach (var provider in Providers)
        {
            foreach (var (name, value) in providerIds)
            {
                if (!string.IsNullOrWhiteSpace(value)
                    && string.Equals(name, provider, StringComparison.OrdinalIgnoreCase))
                {
                    yield return provider.ToLowerInvariant() + "=" + value.Trim();
                }
            }
        }
    }

    private static string EpisodeKey(string seriesKey, int seasonNumber, int episodeNumber)
        => string.Format(CultureInfo.InvariantCulture, "{0}|s{1}e{2}", seriesKey, seasonNumber, episodeNumber);
}
