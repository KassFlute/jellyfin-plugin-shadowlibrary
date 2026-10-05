using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using Jellyfin.Plugin.ShadowLibrary.Sync;
using MediaBrowser.Controller.Library;
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
    // covers ffmpeg's own analysis plus its first HLS segments, so it does not start at 4x
    private const double BurstSeconds = 60;

    // floor for low bitrate files, keeps seeks instant
    private const long MinBurstBytes = 16L * 1024 * 1024;

    // ffprobe and ffmpeg read whatever arrives, a bigger allowance only saturates the link
    private const long MaxBurstBytes = 128L * 1024 * 1024;

    private const double RateFactor = 4;

    // low bitrate files still get a quick refill after a stall
    private const double MinRateBitsPerSecond = 8_000_000;

    private static readonly string[] RelayedHeaders =
        ["Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Content-Disposition"];

    private readonly FriendServerClient _client;
    private readonly FriendServerSessionProvider _sessions;
    private readonly ImportedItemStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamController"/> class.
    /// </summary>
    /// <param name="client">Friend server client.</param>
    /// <param name="sessions">Session provider.</param>
    /// <param name="store">Imported item store.</param>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="logger">Logger.</param>
    public StreamController(
        FriendServerClient client,
        FriendServerSessionProvider sessions,
        ImportedItemStore store,
        ILibraryManager libraryManager,
        ILogger<StreamController> logger)
    {
        _client = client;
        _sessions = sessions;
        _store = store;
        _libraryManager = libraryManager;
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

        // no retry on 401, Jellyfin 10.11 serves static streams without checking the token.
        // A revoked one is renewed by the next cycle, whose listing does check it
        var opened = Stopwatch.GetTimestamp();
        var upstream = await OpenAsync(server, session, item, range, headOnly, cancellationToken).ConfigureAwait(false);
        if (upstream is null)
        {
            return Unreachable(server.Name, "is unreachable");
        }

        using (upstream)
        {
            if (upstream.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("[ShadowLibrary] {Server} no longer holds item {RemoteId}.", server.Name, item.RemoteItemId);
                return StatusCode(StatusCodes.Status410Gone, "The friend server no longer holds this item.");
            }

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
                "[ShadowLibrary] Relaying {RemoteId} from {Server}. Upstream answered {Status}, range {Range}, length {Length}, headers {HeadersMs} ms, client {UserAgent}.",
                item.RemoteItemId,
                server.Name,
                (int)upstream.StatusCode,
                string.IsNullOrEmpty(range) ? "none" : range,
                upstream.Content.Headers.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                (long)Stopwatch.GetElapsedTime(opened).TotalMilliseconds,
                Request.Headers.UserAgent.ToString());

            Response.StatusCode = (int)upstream.StatusCode;
            RelayHeaders(upstream);

            if (headOnly)
            {
                return new EmptyResult();
            }

            long relayed = 0;
            var bodyStarted = Stopwatch.GetTimestamp();
            var bitrate = LocalBitrate(item);
            var rate = RateLimit(bitrate);
            var burst = Burst(bitrate);
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

                        if (rate > 0 && relayed > burst)
                        {
                            // ahead of the allowance, wait until the rate catches up
                            var allowed = burst + (rate * Stopwatch.GetElapsedTime(bodyStarted).TotalSeconds);
                            if (relayed > allowed)
                            {
                                await Task.Delay(TimeSpan.FromSeconds((relayed - allowed) / rate), cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // the player seeked away or closed, nothing to report
                _logger.LogDebug(ex, "[ShadowLibrary] Relay of {RemoteId} ended early.", item.RemoteItemId);
            }

            _logger.LogInformation(
                "[ShadowLibrary] Relay of {RemoteId} closed after {Bytes} bytes in {Seconds:0.0} s, limit {Limit:l}.",
                item.RemoteItemId,
                relayed,
                Stopwatch.GetElapsedTime(bodyStarted).TotalSeconds,
                rate > 0
                    ? (rate * 8 / 1_000_000).ToString("0.0", CultureInfo.InvariantCulture) + " Mbit/s past "
                        + (burst / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB"
                    : "none");

            return new EmptyResult();
        }
    }

    private async Task<HttpResponseMessage?> OpenAsync(
        FriendServer server,
        FriendServerSession session,
        ImportedItem item,
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

    /// <summary>
    /// Bytes per second a relay may sustain once past its burst, or 0 for no limit.
    /// </summary>
    /// <remarks>
    /// Stream copy makes ffmpeg read the input as fast as the network allows, about fifteen
    /// times the playback speed on a fast link, and a player in direct play does the same.
    /// A few times the bitrate keeps them well ahead of playback without holding the link.
    /// </remarks>
    private static double RateLimit(int? bitrate)
    {
        if (bitrate is not > 0)
        {
            // no way to tell what the file needs, starving a 4K remux would be worse
            return 0;
        }

        return Math.Max(bitrate.Value * RateFactor, MinRateBitsPerSecond) / 8d;
    }

    /// <summary>
    /// Bytes a relay may send at full speed before the rate limit applies.
    /// </summary>
    private static long Burst(int? bitrate)
        => bitrate is > 0
            ? Math.Clamp((long)(bitrate.Value / 8d * BurstSeconds), MinBurstBytes, MaxBurstBytes)
            : MinBurstBytes;

    private int? LocalBitrate(ImportedItem item)
        => item.LocalItemId is Guid id ? _libraryManager.GetItemById(id)?.TotalBitrate : null;

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
