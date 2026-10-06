using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Stores the keyframes of imported Matroska files where Jellyfin looks for them.
/// </summary>
/// <remarks>
/// With video copied, ffmpeg can only cut segments and start a resume on the source
/// keyframes. For a local .mkv Jellyfin reads them from the file and lays the HLS playlist
/// and the seek positions out on them. For a .strm it has no file to read and assumes 6 s
/// segments, which match nothing ffmpeg writes, and a resume or a seek never starts. The
/// keyframes are read from the friend server and saved, and the .strm URL ends in .mkv so
/// Jellyfin uses them, see <see cref="MediaFileWriter.BuildStreamUrl"/>.
/// </remarks>
public class KeyframeImporter
{
    // a few small range reads per item, spread out all the same on a first import
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    private readonly ILibraryManager _libraryManager;
    private readonly IKeyframeManager _keyframeManager;
    private readonly FriendServerClient _client;
    private readonly FriendServerSessionProvider _sessions;
    private readonly ILogger<KeyframeImporter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyframeImporter"/> class.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="keyframeManager">Jellyfin keyframe store.</param>
    /// <param name="client">Friend server client.</param>
    /// <param name="sessions">Session provider.</param>
    /// <param name="logger">Logger.</param>
    public KeyframeImporter(
        ILibraryManager libraryManager,
        IKeyframeManager keyframeManager,
        FriendServerClient client,
        FriendServerSessionProvider sessions,
        ILogger<KeyframeImporter> logger)
    {
        _libraryManager = libraryManager;
        _keyframeManager = keyframeManager;
        _client = client;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Imports the keyframes of the Matroska items that have none stored yet.
    /// </summary>
    /// <param name="server">Friend server the items come from.</param>
    /// <param name="items">Imported items to consider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items got their keyframes.</returns>
    public async Task<int> ImportAsync(
        FriendServer server,
        IEnumerable<ImportedItem> items,
        CancellationToken cancellationToken)
    {
        var pending = new List<(ImportedItem Item, Video Video)>();
        foreach (var item in items)
        {
            // the container comes with the tracks, see MediaProbe
            if (item.LocalItemId is Guid id
                && _libraryManager.GetItemById(id) is Video video
                && IsMatroska(video.Container)
                && _keyframeManager.GetKeyframeData(video.Id).Count == 0)
            {
                pending.Add((item, video));
            }
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        var session = await _sessions.GetAsync(server, false, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return 0;
        }

        var imported = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(Pause, cancellationToken).ConfigureAwait(false);
            }

            var (item, video) = pending[i];
            try
            {
                var keyframes = await MatroskaCueReader.ReadAsync(
                    (offset, length, ct) => _client.ReadVideoRangeAsync(
                        session.Url,
                        session.AccessToken,
                        session.DeviceId,
                        item.RemoteItemId,
                        offset,
                        length,
                        ct),
                    video.RunTimeTicks ?? 0,
                    cancellationToken).ConfigureAwait(false);

                if (keyframes.KeyframeTicks.Count == 0)
                {
                    _logger.LogDebug("[ShadowLibrary] {Path} indexes no video keyframe.", video.Path);
                    continue;
                }

                await _keyframeManager.SaveKeyframeDataAsync(video.Id, keyframes, cancellationToken).ConfigureAwait(false);
                imported++;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // tried again next cycle, playback from the start keeps working meanwhile
                _logger.LogWarning(ex, "[ShadowLibrary] Could not read the keyframes of {Path} from {Server}.", video.Path, server.Name);
            }
        }

        if (imported > 0)
        {
            _logger.LogInformation("[ShadowLibrary] Stored the keyframes of {Count} item(s) from {Server}.", imported, server.Name);
        }

        return imported;
    }

    /// <summary>
    /// Whether a Jellyfin container string names Matroska.
    /// </summary>
    /// <param name="container">Container, possibly a comma separated list.</param>
    /// <returns>True for Matroska.</returns>
    internal static bool IsMatroska(string? container)
        => container?.Split(',').Any(c => c.Trim().Equals("mkv", StringComparison.OrdinalIgnoreCase)
            || c.Trim().Equals("matroska", StringComparison.OrdinalIgnoreCase)) == true;
}
