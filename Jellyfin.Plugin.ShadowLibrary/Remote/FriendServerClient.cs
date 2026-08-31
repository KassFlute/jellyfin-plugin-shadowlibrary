using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ShadowLibrary.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// Talks to the API of a friend server: authentication, library discovery and mode detection.
/// </summary>
public class FriendServerClient
{
    /// <summary>
    /// Name of the HTTP client used to relay media. It carries no timeout, since a playback
    /// relay stays open for the length of the film.
    /// </summary>
    public const string StreamClientName = "ShadowLibraryStream";

    private const string ClientName = "ShadowLibrary";
    private const int PageSize = 200;
    private const string MovieFields =
        "Overview,Genres,People,ProviderIds,Studios,Taglines,PremiereDate,OfficialRating,"
        + "CommunityRating,RunTimeTicks,SortName,OriginalTitle";

    private const string SeriesFields =
        "Overview,Genres,People,ProviderIds,Studios,PremiereDate,OfficialRating,"
        + "CommunityRating,SortName,OriginalTitle";

    private const string EpisodeFields =
        "Overview,People,ProviderIds,PremiereDate,CommunityRating,RunTimeTicks";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FriendServerClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendServerClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Host HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public FriendServerClient(IHttpClientFactory httpClientFactory, ILogger<FriendServerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Trims whitespace and any trailing slash from a URL typed by the user.
    /// </summary>
    /// <param name="url">Raw URL.</param>
    /// <returns>The normalised URL, or null when it is not an absolute http(s) URL.</returns>
    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Builds a stable device id for a saved friend server.
    /// </summary>
    /// <param name="friendServerId">Local friend server identifier.</param>
    /// <returns>A deterministic device id.</returns>
    /// <remarks>Stable across cycles so the friend server does not accumulate sessions.</remarks>
    public static string BuildDeviceId(Guid friendServerId)
        => "shadowlibrary-" + friendServerId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a device id for a friend server that has not been saved yet.
    /// </summary>
    /// <param name="url">Friend server URL.</param>
    /// <param name="username">Service account name.</param>
    /// <returns>A deterministic device id.</returns>
    public static string BuildDeviceId(string url, string username)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + username));
        return "shadowlibrary-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>
    /// Runs a full connection test: reachability, authentication, visible libraries and mode.
    /// </summary>
    /// <param name="url">Friend server URL.</param>
    /// <param name="username">Service account name.</param>
    /// <param name="password">Service account password.</param>
    /// <param name="deviceId">Device id to present to the friend server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The test result, including on failure.</returns>
    public async Task<ConnectionTestResult> TestConnectionAsync(
        string url,
        string username,
        string password,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeUrl(url);
        if (normalized is null)
        {
            return Failure("The URL must be absolute and start with http:// or https://.");
        }

        PublicSystemInfo? info;
        try
        {
            info = await GetPublicInfoAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Friend server at {Url} is unreachable.", normalized);
            return Failure("Server unreachable: " + Describe(ex));
        }

        if (info is null)
        {
            return Failure("The address answered but does not look like a Jellyfin server.");
        }

        AuthenticationResult? auth;
        try
        {
            auth = await AuthenticateAsync(normalized, username, password, deviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return Failure("The friend server rejected these credentials.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Authentication against {Url} failed.", normalized);
            return Failure("Authentication failed: " + Describe(ex));
        }

        if (auth?.AccessToken is null || string.IsNullOrEmpty(auth.User?.Id))
        {
            return Failure("The friend server accepted the request but returned no session.");
        }

        var result = new ConnectionTestResult
        {
            Success = true,
            ServerName = info.ServerName,
            ServerVersion = info.Version
        };

        try
        {
            result.Libraries = await GetLibrariesAsync(
                normalized, auth.AccessToken, auth.User.Id, deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Could not list libraries on {Url}.", normalized);
            return Failure("Authenticated, but the library list is not reachable: " + Describe(ex));
        }

        var ping = await TryPingPluginAsync(normalized, auth.AccessToken, deviceId, cancellationToken)
            .ConfigureAwait(false);
        result.Mode = ping is null ? FriendServerMode.Standard : FriendServerMode.Federated;
        result.RemotePluginVersion = ping?.Version;

        var supported = result.Libraries.Count(l => l.IsSupported);
        result.Message = string.Format(
            CultureInfo.InvariantCulture,
            "Connected in {0} mode. {1} library(ies) visible, {2} of them importable (movies and shows).",
            result.Mode == FriendServerMode.Federated ? "federated" : "standard",
            result.Libraries.Length,
            supported);

        return result;
    }

    /// <summary>
    /// Reads the public information of a Jellyfin server, without authentication.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The public information, or null when the response cannot be parsed.</returns>
    public async Task<PublicSystemInfo?> GetPublicInfoAsync(
        string normalizedUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl + "/System/Info/Public");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        try
        {
            return await response.Content
                .ReadFromJsonAsync<PublicSystemInfo>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Authenticates the service account against a friend server.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="username">Service account name.</param>
    /// <param name="password">Service account password.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session token and the matching user.</returns>
    public async Task<AuthenticationResult?> AuthenticateAsync(
        string normalizedUrl,
        string username,
        string password,
        string deviceId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, normalizedUrl + "/Users/AuthenticateByName")
        {
            Content = JsonContent.Create(new { Username = username, Pw = password })
        };
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, null));

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<AuthenticationResult>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the libraries visible to the service account.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="remoteUserId">Service account identifier on the friend server.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The visible libraries.</returns>
    public async Task<RemoteLibrary[]> GetLibrariesAsync(
        string normalizedUrl,
        string accessToken,
        string remoteUserId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var uri = normalizedUrl + "/UserViews?userId=" + Uri.EscapeDataString(remoteUserId);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, accessToken));

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<QueryResult<RemoteLibrary>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return result?.Items ?? Array.Empty<RemoteLibrary>();
    }

    /// <summary>
    /// Checks whether ShadowLibrary runs on the friend server.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The remote plugin response, or null in standard mode.</returns>
    public async Task<FriendPingResponse?> TryPingPluginAsync(
        string normalizedUrl,
        string accessToken,
        string deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, normalizedUrl + "/ShadowLibrary/ping");
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, accessToken));

            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var ping = await response.Content
                .ReadFromJsonAsync<FriendPingResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return string.Equals(ping?.Plugin, ClientName, StringComparison.Ordinal) ? ping : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "[ShadowLibrary] No ShadowLibrary response from {Url}, falling back to standard mode.", normalizedUrl);
            return null;
        }
    }

    /// <summary>
    /// Lists the movies the service account can see on a friend server.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="remoteUserId">Service account identifier on the friend server.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="libraryIds">Libraries to look in, or empty for every visible library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The movies, deduplicated by remote identifier.</returns>
    public Task<IReadOnlyList<RemoteMovie>> GetMoviesAsync(
        string normalizedUrl,
        string accessToken,
        string remoteUserId,
        string deviceId,
        IReadOnlyCollection<string> libraryIds,
        CancellationToken cancellationToken)
        => GetItemsAsync<RemoteMovie>(
            normalizedUrl,
            accessToken,
            remoteUserId,
            deviceId,
            "Movie",
            MovieFields,
            libraryIds,
            m => m.Id,
            cancellationToken);

    /// <summary>
    /// Lists the series the service account can see on a friend server.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="remoteUserId">Service account identifier on the friend server.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="libraryIds">Libraries to look in, or empty for every visible library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series, deduplicated by remote identifier.</returns>
    public Task<IReadOnlyList<RemoteSeries>> GetSeriesAsync(
        string normalizedUrl,
        string accessToken,
        string remoteUserId,
        string deviceId,
        IReadOnlyCollection<string> libraryIds,
        CancellationToken cancellationToken)
        => GetItemsAsync<RemoteSeries>(
            normalizedUrl,
            accessToken,
            remoteUserId,
            deviceId,
            "Series",
            SeriesFields,
            libraryIds,
            s => s.Id,
            cancellationToken);

    /// <summary>
    /// Lists every episode the service account can see on a friend server, in one sweep
    /// rather than one request per series.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="remoteUserId">Service account identifier on the friend server.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="libraryIds">Libraries to look in, or empty for every visible library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episodes, deduplicated by remote identifier.</returns>
    public Task<IReadOnlyList<RemoteEpisode>> GetEpisodesAsync(
        string normalizedUrl,
        string accessToken,
        string remoteUserId,
        string deviceId,
        IReadOnlyCollection<string> libraryIds,
        CancellationToken cancellationToken)
        => GetItemsAsync<RemoteEpisode>(
            normalizedUrl,
            accessToken,
            remoteUserId,
            deviceId,
            "Episode",
            EpisodeFields,
            libraryIds,
            e => e.Id,
            cancellationToken);

    private async Task<IReadOnlyList<T>> GetItemsAsync<T>(
        string normalizedUrl,
        string accessToken,
        string remoteUserId,
        string deviceId,
        string includeItemTypes,
        string fields,
        IReadOnlyCollection<string> libraryIds,
        Func<T, string> idOf,
        CancellationToken cancellationToken)
    {
        var parents = libraryIds.Count == 0 ? new List<string?> { null } : libraryIds.Cast<string?>().ToList();
        var items = new Dictionary<string, T>(StringComparer.Ordinal);

        foreach (var parentId in parents)
        {
            var startIndex = 0;
            while (true)
            {
                var page = await GetItemPageAsync<T>(
                    normalizedUrl,
                    accessToken,
                    remoteUserId,
                    deviceId,
                    includeItemTypes,
                    fields,
                    parentId,
                    startIndex,
                    cancellationToken).ConfigureAwait(false);

                foreach (var item in page.Items)
                {
                    var id = idOf(item);
                    if (!string.IsNullOrEmpty(id))
                    {
                        items[id] = item;
                    }
                }

                startIndex += page.Items.Length;
                if (page.Items.Length == 0 || startIndex >= page.TotalRecordCount)
                {
                    break;
                }
            }
        }

        return items.Values.ToArray();
    }

    private async Task<QueryResult<T>> GetItemPageAsync<T>(
        string normalizedUrl,
        string accessToken,
        string remoteUserId,
        string deviceId,
        string includeItemTypes,
        string fields,
        string? parentId,
        int startIndex,
        CancellationToken cancellationToken)
    {
        var uri = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/Items?userId={1}&Recursive=true&IncludeItemTypes={2}&Fields={3}&SortBy=SortName"
            + "&ExcludeLocationTypes=Virtual&StartIndex={4}&Limit={5}",
            normalizedUrl,
            Uri.EscapeDataString(remoteUserId),
            includeItemTypes,
            fields,
            startIndex,
            PageSize);

        if (!string.IsNullOrEmpty(parentId))
        {
            uri += "&ParentId=" + Uri.EscapeDataString(parentId);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, accessToken));

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<QueryResult<T>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? new QueryResult<T>();
    }

    /// <summary>
    /// Reads the native item identifiers a federated friend server is willing to share.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The native identifiers.</returns>
    public async Task<HashSet<string>> GetNativeItemIdsAsync(
        string normalizedUrl,
        string accessToken,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var startIndex = 0;

        while (true)
        {
            var uri = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/ShadowLibrary/native-items?startIndex={1}&limit={2}",
                normalizedUrl,
                startIndex,
                PageSize);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, accessToken));

            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var page = await response.Content
                .ReadFromJsonAsync<NativeItemsResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (page is null || page.Items.Length == 0)
            {
                break;
            }

            foreach (var id in page.Items)
            {
                ids.Add(id);
            }

            startIndex += page.Items.Length;
            if (startIndex >= page.TotalRecordCount)
            {
                break;
            }
        }

        return ids;
    }

    /// <summary>
    /// Downloads one image of a remote item to a local file.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="remoteItemId">Item identifier on the friend server.</param>
    /// <param name="imageType">Image type, such as Primary, Backdrop or Logo.</param>
    /// <param name="destinationPath">Local file to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the image was written.</returns>
    public async Task<bool> DownloadImageAsync(
        string normalizedUrl,
        string accessToken,
        string deviceId,
        string remoteItemId,
        string imageType,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var uri = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/Items/{1}/Images/{2}",
            normalizedUrl,
            Uri.EscapeDataString(remoteItemId),
            Uri.EscapeDataString(imageType));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, accessToken));

            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return false;
            }

            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogDebug(ex, "[ShadowLibrary] Could not fetch the {Type} image of {ItemId}.", imageType, remoteItemId);
            return false;
        }
    }

    /// <summary>
    /// Asks the friend server for a fresh playback description of an item.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="remoteUserId">Service account identifier on the friend server.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="remoteItemId">Item identifier on the friend server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The playback info, and the status the friend server answered with.</returns>
    public async Task<(HttpStatusCode Status, PlaybackInfoResponse? Info)> GetPlaybackInfoAsync(
        string normalizedUrl,
        string accessToken,
        string remoteUserId,
        string deviceId,
        string remoteItemId,
        CancellationToken cancellationToken)
    {
        var uri = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/Items/{1}/PlaybackInfo?userId={2}",
            normalizedUrl,
            Uri.EscapeDataString(remoteItemId),
            Uri.EscapeDataString(remoteUserId));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, accessToken));

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        try
        {
            var info = await response.Content
                .ReadFromJsonAsync<PlaybackInfoResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return (response.StatusCode, info);
        }
        catch (JsonException)
        {
            return (HttpStatusCode.BadGateway, null);
        }
    }

    /// <summary>
    /// Opens the media stream of a remote item. The caller owns the response and must
    /// dispose it once the body has been relayed.
    /// </summary>
    /// <param name="normalizedUrl">Normalised friend server URL.</param>
    /// <param name="accessToken">Session token.</param>
    /// <param name="deviceId">Device id to present.</param>
    /// <param name="remoteItemId">Item identifier on the friend server.</param>
    /// <param name="mediaSourceId">Media source to read.</param>
    /// <param name="playSessionId">Play session opened by the playback info call.</param>
    /// <param name="range">Range header of the incoming request, relayed as is.</param>
    /// <param name="headOnly">Ask for headers only.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upstream response, headers read, body still open.</returns>
    public async Task<HttpResponseMessage> OpenVideoStreamAsync(
        string normalizedUrl,
        string accessToken,
        string deviceId,
        string remoteItemId,
        string? mediaSourceId,
        string? playSessionId,
        string? range,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        var uri = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/Videos/{1}/stream?static=true&deviceId={2}",
            normalizedUrl,
            Uri.EscapeDataString(remoteItemId),
            Uri.EscapeDataString(deviceId));

        if (!string.IsNullOrEmpty(mediaSourceId))
        {
            uri += "&mediaSourceId=" + Uri.EscapeDataString(mediaSourceId);
        }

        if (!string.IsNullOrEmpty(playSessionId))
        {
            uri += "&playSessionId=" + Uri.EscapeDataString(playSessionId);
        }

        var request = new HttpRequestMessage(headOnly ? HttpMethod.Head : HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(deviceId, accessToken));

        if (!string.IsNullOrEmpty(range))
        {
            // seeking depends on this being passed through untouched
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        var client = _httpClientFactory.CreateClient(StreamClientName);

        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static string BuildAuthHeader(string deviceId, string? accessToken)
    {
        var version = Plugin.Instance?.Version.ToString() ?? "0.0.0.0";
        var header = string.Format(
            CultureInfo.InvariantCulture,
            "MediaBrowser Client=\"{0}\", Device=\"{0}\", DeviceId=\"{1}\", Version=\"{2}\"",
            ClientName,
            Sanitize(deviceId),
            Sanitize(version));

        return accessToken is null
            ? header
            : header + string.Format(CultureInfo.InvariantCulture, ", Token=\"{0}\"", Sanitize(accessToken));
    }

    private static string Sanitize(string value)
        => value.Replace("\"", string.Empty, StringComparison.Ordinal)
                .Replace("\\", string.Empty, StringComparison.Ordinal);

    private static ConnectionTestResult Failure(string message)
        => new() { Success = false, Message = message };

    private static string Describe(Exception ex)
        => ex is TaskCanceledException ? "timed out." : ex.Message;

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        // buffer the body inside the timeout window, responses are small
        return await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
    }
}
