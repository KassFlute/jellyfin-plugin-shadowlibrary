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
    private readonly ILogger<StreamController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamController"/> class.
    /// </summary>
    /// <param name="client">Friend server client.</param>
    /// <param name="sessions">Session provider.</param>
    /// <param name="store">Imported item store.</param>
    /// <param name="logger">Logger.</param>
    public StreamController(
        FriendServerClient client,
        FriendServerSessionProvider sessions,
        ImportedItemStore store,
        ILogger<StreamController> logger)
    {
        _client = client;
        _sessions = sessions;
        _store = store;
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
                session = await _sessions.GetAsync(server, true, cancellationToken).ConfigureAwait(false);
                if (session is null)
                {
                    return Unreachable(server.Name, "refused the service account");
                }

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
                return StatusCode(StatusCodes.Status410Gone, "The friend server no longer holds this item.");
            }

            if (payload is null)
            {
                return Unreachable(server.Name, "answered " + (int)status + " to the playback request");
            }

            info = payload;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Playback request to {Server} failed.", server.Name);
            return Unreachable(server.Name, "is unreachable");
        }

        var source = Array.Find(info.MediaSources, s => s.SupportsDirectStream) ?? info.MediaSources.FirstOrDefault();
        if (source is null)
        {
            _logger.LogWarning("[ShadowLibrary] {Server} returned no media source for {RemoteId}.", server.Name, item.RemoteItemId);
            return StatusCode(StatusCodes.Status410Gone, "The friend server returned no playable source for this item.");
        }

        var range = Request.Headers.Range.ToString();

        HttpResponseMessage upstream;
        try
        {
            upstream = await _client.OpenVideoStreamAsync(
                session.Url,
                session.AccessToken,
                session.DeviceId,
                item.RemoteItemId,
                source.Id,
                info.PlaySessionId,
                range,
                HttpMethods.IsHead(Request.Method),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Opening the stream of {RemoteId} on {Server} failed.", item.RemoteItemId, server.Name);
            return Unreachable(server.Name, "is unreachable");
        }

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[ShadowLibrary] {Server} answered {Status} when asked for the media of {RemoteId}.",
                    server.Name,
                    (int)upstream.StatusCode,
                    item.RemoteItemId);
                return Unreachable(server.Name, "answered " + (int)upstream.StatusCode + " to the media request");
            }

            _logger.LogInformation(
                "[ShadowLibrary] Relaying {RemoteId} from {Server}. Upstream answered {Status}, range {Range}, length {Length}.",
                item.RemoteItemId,
                server.Name,
                (int)upstream.StatusCode,
                string.IsNullOrEmpty(range) ? "none" : range,
                upstream.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown");

            Response.StatusCode = (int)upstream.StatusCode;
            RelayHeaders(upstream);

            if (HttpMethods.IsHead(Request.Method))
            {
                return new EmptyResult();
            }

            try
            {
                var body = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    await body.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // the player seeked away or closed, nothing to report
                _logger.LogDebug(ex, "[ShadowLibrary] Relay of {RemoteId} ended early.", item.RemoteItemId);
            }

            return new EmptyResult();
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
