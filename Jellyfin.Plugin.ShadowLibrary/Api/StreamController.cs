using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using Jellyfin.Plugin.ShadowLibrary.Sync;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Api;

/// <summary>
/// Playback proxy. Generated .strm files point here, never at the friend server, so the
/// friend token never reaches the disk or the client.
/// </summary>
/// <remarks>
/// Anonymous on purpose. Whoever ends up fetching the .strm URL, the local media pipeline
/// or a player, has no ShadowLibrary session to present, so the URL carries a key of its own.
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("ShadowLibrary/stream")]
public class StreamController : ControllerBase
{
    private static readonly string[] RelayedHeaders =
        ["Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Content-Disposition"];

    private readonly FriendServerClient _client;
    private readonly FriendServerSessionProvider _sessions;
    private readonly ImportedItemStore _store;
    private readonly PlaybackTargetCache _targets;
    private readonly ILogger<StreamController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamController"/> class.
    /// </summary>
    /// <param name="client">Friend server client.</param>
    /// <param name="sessions">Session provider.</param>
    /// <param name="store">Imported item store.</param>
    /// <param name="targets">Sources already resolved for recent relays.</param>
    /// <param name="logger">Logger.</param>
    public StreamController(
        FriendServerClient client,
        FriendServerSessionProvider sessions,
        ImportedItemStore store,
        PlaybackTargetCache targets,
        ILogger<StreamController> logger)
    {
        _client = client;
        _sessions = sessions;
        _store = store;
        _targets = targets;
        _logger = logger;
    }

    /// <summary>
    /// Relays the media of an imported item from the friend server that holds it.
    /// </summary>
    /// <param name="itemId">Plugin side item identifier, the one written in the .strm.</param>
    /// <param name="key">Key carried by the .strm URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Media relayed.</response>
    /// <response code="206">Requested range relayed.</response>
    /// <response code="401">Missing or wrong key.</response>
    /// <response code="404">Unknown item.</response>
    /// <response code="410">The friend server no longer holds this item.</response>
    /// <response code="502">The friend server could not be reached.</response>
    /// <returns>The relayed media.</returns>
    [HttpGet("{itemId:guid}")]
    [HttpHead("{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> GetStream(
        [FromRoute] Guid itemId,
        [FromQuery] string? key,
        CancellationToken cancellationToken)
    {
        if (!IsKeyValid(key))
        {
            return Unauthorized();
        }

        var item = _store.GetById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var server = Array.Find(ConfigurationStore.Current.FriendServers, s => s.Id == item.FriendServerId);
        if (server is null)
        {
            _logger.LogWarning("[ShadowLibrary] Item {ItemId} points at friend server {ServerId}, which is gone.", itemId, item.FriendServerId);
            return StatusCode(StatusCodes.Status410Gone, "The friend server this item comes from is no longer configured.");
        }

        var session = await _sessions.GetAsync(server, false, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Unreachable(server.Name, "could not be authenticated against");
        }

        var range = Request.Headers.Range.ToString();
        var headOnly = HttpMethods.IsHead(Request.Method);
        var started = Stopwatch.GetTimestamp();

        var cached = _targets.TryGet(itemId, out var target);
        if (!cached)
        {
            var resolved = await ResolveAsync(server, session, item, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                return resolved.Error;
            }

            (session, target) = (resolved.Session, resolved.Target);
            _targets.Set(itemId, target);
        }

        var infoElapsed = Stopwatch.GetElapsedTime(started);

        var upstream = await OpenAsync(server, session, item, target, range, headOnly, cancellationToken).ConfigureAwait(false);
        if (upstream is not null && !upstream.IsSuccessStatusCode && cached)
        {
            // the friend server may have dropped the play session or the token, start over once
            _logger.LogInformation(
                "[ShadowLibrary] {Server} answered {Status} to a cached source of {RemoteId}, asking again.",
                server.Name,
                (int)upstream.StatusCode,
                item.RemoteItemId);
            upstream.Dispose();
            _targets.Remove(itemId);
            cached = false;

            var resolved = await ResolveAsync(server, session, item, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                return resolved.Error;
            }

            (session, target) = (resolved.Session, resolved.Target);
            _targets.Set(itemId, target);
            infoElapsed = Stopwatch.GetElapsedTime(started);

            upstream = await OpenAsync(server, session, item, target, range, headOnly, cancellationToken).ConfigureAwait(false);
        }

        if (upstream is null)
        {
            return Unreachable(server.Name, "is unreachable");
        }

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
            {
                _targets.Remove(itemId);
                _logger.LogWarning(
                    "[ShadowLibrary] {Server} answered {Status} when asked for the media of {RemoteId}.",
                    server.Name,
                    (int)upstream.StatusCode,
                    item.RemoteItemId);
                return Unreachable(server.Name, "answered " + (int)upstream.StatusCode + " to the media request");
            }

            var headersElapsed = Stopwatch.GetElapsedTime(started) - infoElapsed;

            _logger.LogInformation(
                "[ShadowLibrary] Relaying {RemoteId} from {Server}. Upstream answered {Status}, range {Range}, length {Length}. Playback info {Info:l}, headers {HeadersMs} ms.",
                item.RemoteItemId,
                server.Name,
                (int)upstream.StatusCode,
                string.IsNullOrEmpty(range) ? "none" : range,
                upstream.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                cached ? "cached" : ((long)infoElapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms",
                (long)headersElapsed.TotalMilliseconds);

            Response.StatusCode = (int)upstream.StatusCode;
            RelayHeaders(upstream);

            if (headOnly)
            {
                return new EmptyResult();
            }

            long relayed = 0;
            var bodyStarted = Stopwatch.GetTimestamp();
            try
            {
                var body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        relayed += read;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // the player seeked away or closed, nothing to report
                _logger.LogDebug(ex, "[ShadowLibrary] Relay of {RemoteId} ended early.", item.RemoteItemId);
            }

            _logger.LogInformation(
                "[ShadowLibrary] Relay of {RemoteId} closed after {Bytes} bytes in {Seconds:0.0} s.",
                item.RemoteItemId,
                relayed,
                Stopwatch.GetElapsedTime(bodyStarted).TotalSeconds);

            return new EmptyResult();
        }
    }

    /// <summary>
    /// Asks the friend server which source to read and opens a play session for it.
    /// </summary>
    private async Task<(ActionResult? Error, FriendServerSession Session, PlaybackTarget Target)> ResolveAsync(
        FriendServer server,
        FriendServerSession session,
        ImportedItem item,
        CancellationToken cancellationToken)
    {
        PlaybackInfoResponse? info;
        try
        {
            var (status, payload) = await _client.GetPlaybackInfoAsync(
                session.Url,
                session.AccessToken,
                session.RemoteUserId,
                session.DeviceId,
                item.RemoteItemId,
                cancellationToken).ConfigureAwait(false);

            if (status == HttpStatusCode.Unauthorized)
            {
                // the stored token was revoked or expired, one retry with a fresh session
                var refreshed = await _sessions.GetAsync(server, true, cancellationToken).ConfigureAwait(false);
                if (refreshed is null)
                {
                    return (Unreachable(server.Name, "refused the service account"), session, default);
                }

                session = refreshed;
                (status, payload) = await _client.GetPlaybackInfoAsync(
                    session.Url,
                    session.AccessToken,
                    session.RemoteUserId,
                    session.DeviceId,
                    item.RemoteItemId,
                    cancellationToken).ConfigureAwait(false);
            }

            if (status == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("[ShadowLibrary] {Server} no longer holds item {RemoteId}.", server.Name, item.RemoteItemId);
                return (StatusCode(StatusCodes.Status410Gone, "The friend server no longer holds this item."), session, default);
            }

            if (payload is null)
            {
                return (Unreachable(server.Name, "answered " + (int)status + " to the playback request"), session, default);
            }

            info = payload;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Playback request to {Server} failed.", server.Name);
            return (Unreachable(server.Name, "is unreachable"), session, default);
        }

        var source = Array.Find(info.MediaSources, s => s.SupportsDirectStream) ?? info.MediaSources.FirstOrDefault();
        if (source is null)
        {
            _logger.LogWarning("[ShadowLibrary] {Server} returned no media source for {RemoteId}.", server.Name, item.RemoteItemId);
            return (StatusCode(StatusCodes.Status410Gone, "The friend server returned no playable source for this item."), session, default);
        }

        return (null, session, new PlaybackTarget(source.Id, info.PlaySessionId));
    }

    private async Task<HttpResponseMessage?> OpenAsync(
        FriendServer server,
        FriendServerSession session,
        ImportedItem item,
        PlaybackTarget target,
        string range,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.OpenVideoStreamAsync(
                session.Url,
                session.AccessToken,
                session.DeviceId,
                item.RemoteItemId,
                target.MediaSourceId,
                target.PlaySessionId,
                range,
                headOnly,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Opening the stream of {RemoteId} on {Server} failed.", item.RemoteItemId, server.Name);
            return null;
        }
    }

    private static bool IsKeyValid(string? key)
    {
        var expected = ConfigurationStore.Current.StreamAccessKey;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(key))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(expected));
    }

    private void RelayHeaders(HttpResponseMessage upstream)
    {
        foreach (var name in RelayedHeaders)
        {
            if (upstream.Content.Headers.TryGetValues(name, out var values)
                || upstream.Headers.TryGetValues(name, out values))
            {
                Response.Headers[name] = values.ToArray();
            }
        }
    }

    private ObjectResult Unreachable(string serverName, string reason)
        => StatusCode(
            StatusCodes.Status502BadGateway,
            $"The friend server {serverName} {reason}. Playback cannot start.");
}
