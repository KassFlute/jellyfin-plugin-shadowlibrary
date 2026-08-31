using Jellyfin.Plugin.ShadowLibrary.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Asks Jellyfin to inspect imported media so audio and subtitle tracks are known before
/// anyone plays them.
/// </summary>
/// <remarks>
/// Jellyfin skips inspecting a .strm during a library scan and only does it on the first
/// playback request. Doing it here means the tracks are listed on the item page right away.
/// </remarks>
public class MediaProbe
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MediaProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaProbe"/> class.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public MediaProbe(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IFileSystem fileSystem,
        ILogger<MediaProbe> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Inspects the imported items Jellyfin has scanned but not yet looked inside.
    /// </summary>
    /// <param name="items">Imported items to consider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were inspected.</returns>
    public async Task<int> ProbeAsync(IEnumerable<ImportedItem> items, CancellationToken cancellationToken)
    {
        var probed = 0;

        foreach (var stored in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stored.LocalItemId is null)
            {
                // Jellyfin has not scanned the .strm yet, it will be picked up next cycle
                continue;
            }

            var item = _libraryManager.GetItemById(stored.LocalItemId.Value);
            if (item is null)
            {
                continue;
            }

            if (_mediaSourceManager.GetMediaStreams(item.Id).Any(s => s.Type == MediaStreamType.Video))
            {
                continue;
            }

            try
            {
                // FullRefresh is required, not a preference. Below it, MetadataService keeps
                // only the providers whose HasChanged reports something, and the file inspector
                // reports nothing for a .strm that has not been touched since the scan.
                await item.RefreshMetadata(
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        EnableRemoteContentProbe = true,
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                    },
                    cancellationToken).ConfigureAwait(false);

                probed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ShadowLibrary] Could not inspect {Path}.", stored.StrmPath);
            }
        }

        if (probed > 0)
        {
            _logger.LogInformation("[ShadowLibrary] Inspected {Count} newly imported item(s).", probed);
        }

        return probed;
    }
}
