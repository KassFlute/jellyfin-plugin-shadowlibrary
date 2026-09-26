using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Fills in the audio and subtitle tracks of imported media before anyone plays them.
/// </summary>
/// <remarks>
/// Jellyfin skips inspecting a .strm during a library scan and only does it on the first
/// playback request. The friend server already inspected the file, so its answer is written
/// as is. Running ffprobe instead pulls every file through the relay back to back, which
/// saturates the uplink for the length of the scan.
/// </remarks>
public class MediaProbe
{
    // ffprobe fallback only, spreads the transfers out
    private static readonly TimeSpan FallbackPause = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly IMediaAttachmentRepository _mediaAttachmentRepository;
    private readonly IChapterManager _chapterManager;
    private readonly FriendServerClient _client;
    private readonly FriendServerSessionProvider _sessions;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MediaProbe> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaProbe"/> class.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="mediaSourceManager">Media source manager.</param>
    /// <param name="mediaStreamRepository">Media stream store.</param>
    /// <param name="mediaAttachmentRepository">Media attachment store.</param>
    /// <param name="chapterManager">Chapter store.</param>
    /// <param name="client">Friend server client.</param>
    /// <param name="sessions">Session provider.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public MediaProbe(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaStreamRepository mediaStreamRepository,
        IMediaAttachmentRepository mediaAttachmentRepository,
        IChapterManager chapterManager,
        FriendServerClient client,
        FriendServerSessionProvider sessions,
        IFileSystem fileSystem,
        ILogger<MediaProbe> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaStreamRepository = mediaStreamRepository;
        _mediaAttachmentRepository = mediaAttachmentRepository;
        _chapterManager = chapterManager;
        _client = client;
        _sessions = sessions;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Fills in the tracks of the imported items Jellyfin has scanned but knows nothing about.
    /// </summary>
    /// <param name="server">Friend server the items come from.</param>
    /// <param name="items">Imported items to consider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were filled in.</returns>
    public async Task<int> ProbeAsync(
        FriendServer server,
        IEnumerable<ImportedItem> items,
        CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, Video>(StringComparer.Ordinal);

        foreach (var stored in items)
        {
            if (stored.LocalItemId is null)
            {
                // Jellyfin has not scanned the .strm yet, it will be picked up next cycle
                continue;
            }

            if (_libraryManager.GetItemById(stored.LocalItemId.Value) is not Video video)
            {
                continue;
            }

            if (_mediaSourceManager.GetMediaStreams(video.Id).Any(s => s.Type == MediaStreamType.Video))
            {
                continue;
            }

            pending[stored.RemoteItemId] = video;
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        var remote = await GetRemoteMediaInfoAsync(server, pending.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            // nothing written, the items still lack a video stream and come back next cycle
            return 0;
        }

        var copied = 0;
        var fallback = new List<Video>();

        foreach (var (remoteId, video) in pending)
        {
            var source = remote.TryGetValue(remoteId, out var info) ? PickSource(info) : null;
            if (source is null)
            {
                fallback.Add(video);
                continue;
            }

            try
            {
                await ApplyAsync(video, source, info!.Chapters ?? [], cancellationToken).ConfigureAwait(false);
                copied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[ShadowLibrary] Could not store the tracks of {Path}.", video.Path);
            }
        }

        var probed = await ProbeLocallyAsync(fallback, cancellationToken).ConfigureAwait(false);

        if (copied + probed > 0)
        {
            _logger.LogInformation(
                "[ShadowLibrary] Filled in the tracks of {Copied} item(s) from {Server}, inspected {Probed} with ffprobe.",
                copied,
                server.Name,
                probed);
        }

        return copied + probed;
    }

    private static MediaSourceInfo? PickSource(RemoteMediaInfo info)
    {
        // the relay streams this same source, see StreamController
        var sources = info.MediaSources ?? [];
        var source = Array.Find(sources, s => s.SupportsDirectStream) ?? sources.FirstOrDefault();

        // no video stream means the friend never inspected the file either
        return source?.MediaStreams?.Any(s => s.Type == MediaStreamType.Video) == true ? source : null;
    }

    private async Task<Dictionary<string, RemoteMediaInfo>?> GetRemoteMediaInfoAsync(
        FriendServer server,
        IReadOnlyList<string> remoteIds,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(server, false, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        try
        {
            var infos = await _client.GetMediaInfoAsync(
                session.Url,
                session.AccessToken,
                session.RemoteUserId,
                session.DeviceId,
                remoteIds,
                cancellationToken).ConfigureAwait(false);

            return infos
                .Where(i => !string.IsNullOrEmpty(i.Id))
                .GroupBy(i => i.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Could not read the tracks of the new items from {Server}.", server.Name);
            return null;
        }
    }

    /// <summary>
    /// Writes what the friend server knows, the way FFProbeVideoInfo.Fetch writes a probe result.
    /// </summary>
    private async Task ApplyAsync(
        Video video,
        MediaSourceInfo source,
        ChapterInfo[] chapters,
        CancellationToken cancellationToken)
    {
        // external streams are files on the friend server disk, the relay cannot serve them.
        // What is left gets the indexes a local ffprobe would give
        var streams = source.MediaStreams.Where(s => !s.IsExternal).ToList();
        for (var i = 0; i < streams.Count; i++)
        {
            streams[i].Index = i;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(video);
        var allowed = libraryOptions.AllowEmbeddedSubtitles;
        if (allowed is EmbeddedSubtitleOptions.AllowText or EmbeddedSubtitleOptions.AllowNone)
        {
            streams.RemoveAll(s => s.Type == MediaStreamType.Subtitle && !s.IsTextSubtitleStream);
        }

        if (allowed is EmbeddedSubtitleOptions.AllowImage or EmbeddedSubtitleOptions.AllowNone)
        {
            streams.RemoveAll(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream);
        }

        var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

        video.TotalBitrate = source.Bitrate;
        video.RunTimeTicks = source.RunTimeTicks ?? video.RunTimeTicks;
        video.Container = source.Container;
        video.Timestamp = source.Timestamp;
        video.Video3DFormat ??= source.Video3DFormat;
        video.Height = videoStream?.Height ?? 0;
        video.Width = videoStream?.Width ?? 0;
        video.DefaultVideoStreamIndex = videoStream?.Index;
        video.HasSubtitles = streams.Any(s => s.Type == MediaStreamType.Subtitle);

        _mediaStreamRepository.SaveMediaStreams(video.Id, streams, cancellationToken);
        _mediaAttachmentRepository.SaveMediaAttachments(video.Id, source.MediaAttachments ?? [], cancellationToken);

        // names and positions only, images would mean reading the whole file through the relay
        _chapterManager.SaveChapters(
            video,
            chapters.Select(c => new ChapterInfo { StartPositionTicks = c.StartPositionTicks, Name = c.Name }).ToArray());

        await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ProbeLocallyAsync(List<Video> videos, CancellationToken cancellationToken)
    {
        var probed = 0;

        for (var i = 0; i < videos.Count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(FallbackPause, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                // FullRefresh is required, not a preference. Below it, MetadataService keeps
                // only the providers whose HasChanged reports something, and the file inspector
                // reports nothing for a .strm that has not been touched since the scan.
                await videos[i].RefreshMetadata(
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
                _logger.LogWarning(ex, "[ShadowLibrary] Could not inspect {Path}.", videos[i].Path);
            }
        }

        return probed;
    }
}
