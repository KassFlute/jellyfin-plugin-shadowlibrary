using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Has Jellyfin read the metadata of imported items again, under every mount they are scanned
/// from.
/// </summary>
public class MetadataRefresher
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MetadataRefresher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataRefresher"/> class.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="providerManager">Metadata provider manager.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public MetadataRefresher(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<MetadataRefresher> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Queues a full refresh of the items generated from a set of imports.
    /// </summary>
    /// <param name="importedIds">Plugin side item identifiers.</param>
    /// <param name="fromNfo">Whether the .nfo holds the metadata, rather than only identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that ends once everything is queued.</returns>
    public async Task QueueAsync(IEnumerable<Guid> importedIds, bool fromNfo, CancellationToken cancellationToken)
    {
        var items = ImportedItems.Find(_libraryManager);
        var targets = new HashSet<Guid>();

        foreach (var item in importedIds.SelectMany(id => items[id]))
        {
            targets.Add(item.Id);
            if (item is Episode episode)
            {
                targets.Add(episode.SeriesId);
                targets.Add(episode.SeasonId);
            }
        }

        targets.Remove(Guid.Empty);

        foreach (var id in targets)
        {
            // a replace lets the remote title of an episode win over its .nfo. A first refresh in
            // Default mode is what a scan does with a new item, and there the .nfo wins
            if (fromNfo && _libraryManager.GetItemById(id) is BaseItem item)
            {
                item.DateLastRefreshed = DateTime.MinValue;
                await item.UpdateToRepositoryAsync(ItemUpdateType.None, cancellationToken).ConfigureAwait(false);
            }

            _providerManager.QueueRefresh(
                id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = fromNfo ? MetadataRefreshMode.Default : MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = !fromNfo,
                    ReplaceAllImages = true
                },
                RefreshPriority.Low);
        }

        _logger.LogInformation("[ShadowLibrary] Queued a metadata refresh of {Count} item(s).", targets.Count);
    }

    /// <summary>
    /// Drops the collection id earlier versions copied from friend servers, where it is the id
    /// of the movie itself. A real one is left to Jellyfin.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were fixed.</returns>
    public async Task<int> DropCopiedCollectionIdsAsync(CancellationToken cancellationToken)
    {
        var fixedCount = 0;
        foreach (var video in ImportedItems.Find(_libraryManager).SelectMany(g => g))
        {
            var collection = video.GetProviderId(MetadataProvider.TmdbCollection);
            if (string.IsNullOrEmpty(collection) || collection != video.GetProviderId(MetadataProvider.Tmdb))
            {
                continue;
            }

            video.ProviderIds.Remove(MetadataProvider.TmdbCollection.ToString());
            await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            fixedCount++;
        }

        if (fixedCount > 0)
        {
            _logger.LogInformation("[ShadowLibrary] Dropped the copied collection id of {Count} item(s).", fixedCount);
        }

        return fixedCount;
    }
}
