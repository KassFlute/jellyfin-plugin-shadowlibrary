using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Playback;

/// <summary>
/// Wraps Jellyfin's media source manager to start imported items without the work a local
/// file needs, everything else goes to the wrapped manager untouched.
/// </summary>
/// <remarks>
/// Jellyfin re-probes every .strm on each playback request and ffmpeg then analyses 200 s of
/// input by default. Through the relay both cost seconds of transfer. Imported items already
/// carry the tracks the friend server found (see MediaProbe), so the re-probe is skipped and
/// ffmpeg gets a short analysis, for these items only.
/// </remarks>
public sealed class ShadowMediaSourceManager : IMediaSourceManager
{
    // enough for ffmpeg to find the video and audio parameters. Image subtitles without a
    // frame in that window lose their size and burn-in aborts, see CanShortenAnalysis
    private const int AnalyzeDurationMs = 10_000;

    private const string RelayPathMarker = "/ShadowLibrary/stream/";

    private readonly IMediaSourceManager _inner;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<ShadowMediaSourceManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowMediaSourceManager"/> class.
    /// </summary>
    /// <param name="inner">Jellyfin's own media source manager.</param>
    /// <param name="httpContextAccessor">Access to the playback request being served, if any.</param>
    /// <param name="logger">Logger.</param>
    public ShadowMediaSourceManager(
        IMediaSourceManager inner,
        IHttpContextAccessor? httpContextAccessor,
        ILogger<ShadowMediaSourceManager> logger)
    {
        _inner = inner;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _logger.LogInformation("[ShadowLibrary] Playback of imported items skips the .strm re-probe ({Inner}).", inner.GetType().FullName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User? user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (!IsImported(item))
        {
            return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        }

        // without a video track the probe is the only way to learn the tracks, let it run
        var skipProbe = allowMediaProbe && HasVideoStream(item);
        if (skipProbe)
        {
            _logger.LogInformation("[ShadowLibrary] Skipped the re-probe of {Path}.", item.Path);
        }

        var sources = await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe && !skipProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        if (CanShortenAnalysis(item))
        {
            foreach (var source in sources)
            {
                ShortenAnalysis(source);
            }
        }

        return sources;
    }

    /// <inheritdoc />
    public async Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string? mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        var source = await _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
        if (IsImported(item) && CanShortenAnalysis(item))
        {
            ShortenAnalysis(source);
        }

        return source;
    }

    /// <inheritdoc />
    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => _inner.GetMediaStreams(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);

    /// <inheritdoc />
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);

    /// <inheritdoc />
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
        => _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

    /// <inheritdoc />
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStream(request, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken)
        => _inner.OpenLiveStreamInternal(request, cancellationToken);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStream(id, cancellationToken);

    /// <inheritdoc />
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    /// <inheritdoc />
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken)
        => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    /// <inheritdoc />
    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    /// <inheritdoc />
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
        => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    /// <inheritdoc />
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => _inner.SupportsDirectStream(path, protocol);

    /// <inheritdoc />
    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    /// <inheritdoc />
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
        => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    /// <inheritdoc />
    public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken)
        => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);

    // the .strm content is the relay URL, nothing else points there
    private static bool IsImported(BaseItem? item)
        => item is Video { IsShortcut: true } video
            && video.ShortcutPath?.Contains(RelayPathMarker, StringComparison.OrdinalIgnoreCase) == true;

    private static void ShortenAnalysis(MediaSourceInfo? source)
    {
        if (source is not null && source.AnalyzeDurationMs is not > 0)
        {
            source.AnalyzeDurationMs = AnalyzeDurationMs;
        }
    }

    private bool HasVideoStream(BaseItem item)
        => _inner.GetMediaStreams(item.Id).Any(s => s.Type == MediaStreamType.Video);

    /// <summary>
    /// Whether ffmpeg can do without the full analysis, true unless an image subtitle may be
    /// burnt in. Those need the size of their frames, which only the long analysis finds.
    /// </summary>
    private bool CanShortenAnalysis(BaseItem item)
    {
        var imageSubtitles = _inner.GetMediaStreams(item.Id)
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsTextSubtitleStream)
            .Select(s => s.Index)
            .ToHashSet();
        if (imageSubtitles.Count == 0)
        {
            return true;
        }

        // the streaming request names the subtitle to burn in, the playback info request
        // does not matter since its sources are not used to encode
        var query = _httpContextAccessor?.HttpContext?.Request.Query;
        if (query is null)
        {
            return false;
        }

        if (!int.TryParse(query["SubtitleStreamIndex"], CultureInfo.InvariantCulture, out var index)
            || !imageSubtitles.Contains(index))
        {
            return true;
        }

        _logger.LogInformation("[ShadowLibrary] Kept the full analysis of {Path}, image subtitle {Index} is burnt in.", item.Path, index);
        return false;
    }
}
