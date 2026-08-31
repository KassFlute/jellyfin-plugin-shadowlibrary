using System.Net;
using System.Net.Mime;
using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Security;
using Jellyfin.Plugin.ShadowLibrary.Sync;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Api;

/// <summary>
/// Friend server management from the dashboard.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("ShadowLibrary/FriendServers")]
[Produces(MediaTypeNames.Application.Json)]
public class FriendServersController : ControllerBase
{
    // saving must stay responsive, an unreachable server just leaves the mode unknown
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(15);

    private readonly FriendServerClient _client;
    private readonly SecretStore _secretStore;
    private readonly ImportedMediaCleaner _cleaner;
    private readonly LibraryAttacher _attacher;
    private readonly MediaFileWriter _writer;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FriendServersController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendServersController"/> class.
    /// </summary>
    /// <param name="client">Friend server client.</param>
    /// <param name="secretStore">Credential encryption.</param>
    /// <param name="cleaner">Imported media cleaner.</param>
    /// <param name="attacher">Library attacher.</param>
    /// <param name="writer">File writer, asked for the effective stream URL.</param>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="logger">Logger.</param>
    public FriendServersController(
        FriendServerClient client,
        SecretStore secretStore,
        ImportedMediaCleaner cleaner,
        LibraryAttacher attacher,
        MediaFileWriter writer,
        ILibraryManager libraryManager,
        ILogger<FriendServersController> logger)
    {
        _client = client;
        _secretStore = secretStore;
        _cleaner = cleaner;
        _attacher = attacher;
        _writer = writer;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Lists the configured friend servers.
    /// </summary>
    /// <response code="200">List returned.</response>
    /// <returns>The friend servers, without any secret.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FriendServerDto[]> GetFriendServers()
        => ConfigurationStore.Current.FriendServers.Select(ToDto).ToArray();

    /// <summary>
    /// Lists the libraries of this server a friend folder can be attached to.
    /// </summary>
    /// <response code="200">List returned.</response>
    /// <returns>The movie and show libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LocalLibrary>> GetLibraries()
        => Ok(_attacher.GetAttachableLibraries());

    /// <summary>
    /// Adds a friend server.
    /// </summary>
    /// <param name="request">Friend server to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Friend server added.</response>
    /// <response code="400">Invalid request.</response>
    /// <returns>The created friend server.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FriendServerDto>> AddFriendServer(
        [FromBody] FriendServerRequest request,
        CancellationToken cancellationToken)
    {
        var url = FriendServerClient.NormalizeUrl(request.Url);
        if (url is null)
        {
            return BadRequest("The URL must be absolute and start with http:// or https://.");
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("A password is required when adding a friend server.");
        }

        var name = request.Name.Trim();
        var server = new FriendServer
        {
            Name = name,
            Url = url,
            Username = request.Username.Trim(),
            EncryptedPassword = _secretStore.Protect(request.Password),
            Enabled = request.Enabled,
            SyncAllLibraries = request.SyncAllLibraries,
            LibraryIds = request.LibraryIds,
            MovieLibraryName = request.MovieLibraryName.Trim(),
            ShowLibraryName = request.ShowLibraryName.Trim()
        };

        ConfigurationStore.Update(config =>
        {
            // frozen here and never recomputed, so renaming the entry later moves no file
            server.FolderName = MediaFileWriter.ReserveFolderName(
                name, server.Id, config.FriendServers.Select(MediaFileWriter.BuildFolderName));

            config.FriendServers = [.. config.FriendServers, server];
        });

        _logger.LogInformation("[ShadowLibrary] Added friend server {Name} ({Url}).", server.Name, server.Url);

        await ProbeAsync(server, request.Password, cancellationToken).ConfigureAwait(false);
        return ToDto(server);
    }

    /// <summary>
    /// Updates a friend server.
    /// </summary>
    /// <param name="id">Local friend server identifier.</param>
    /// <param name="request">New values. An empty password keeps the stored one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Friend server updated.</response>
    /// <response code="400">Invalid request.</response>
    /// <response code="404">Unknown friend server.</response>
    /// <returns>The updated friend server.</returns>
    [HttpPost("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FriendServerDto>> UpdateFriendServer(
        [FromRoute] Guid id,
        [FromBody] FriendServerRequest request,
        CancellationToken cancellationToken)
    {
        var url = FriendServerClient.NormalizeUrl(request.Url);
        if (url is null)
        {
            return BadRequest("The URL must be absolute and start with http:// or https://.");
        }

        FriendServer? server = null;
        string? password = null;

        ConfigurationStore.Update(config =>
        {
            var found = Array.Find(config.FriendServers, s => s.Id == id);
            if (found is null)
            {
                return;
            }

            server = found;
            var credentialsChanged = !string.Equals(server.Url, url, StringComparison.Ordinal)
                || !string.Equals(server.Username, request.Username.Trim(), StringComparison.Ordinal)
                || !string.IsNullOrEmpty(request.Password);

            server.Name = request.Name.Trim();
            server.Url = url;
            server.Username = request.Username.Trim();
            server.Enabled = request.Enabled;
            server.SyncAllLibraries = request.SyncAllLibraries;
            server.LibraryIds = request.LibraryIds;
            server.MovieLibraryName = request.MovieLibraryName.Trim();
            server.ShowLibraryName = request.ShowLibraryName.Trim();

            if (!string.IsNullOrEmpty(request.Password))
            {
                server.EncryptedPassword = _secretStore.Protect(request.Password);
            }

            if (credentialsChanged)
            {
                // the stored session belongs to the old url or account
                server.EncryptedAccessToken = string.Empty;
                server.RemoteUserId = string.Empty;
                server.LastKnownMode = FriendServerMode.Unknown;
            }

            password = string.IsNullOrEmpty(request.Password)
                ? _secretStore.Unprotect(server.EncryptedPassword)
                : request.Password;
        });

        if (server is null)
        {
            return NotFound();
        }

        _logger.LogInformation("[ShadowLibrary] Updated friend server {Name} ({Url}).", server.Name, server.Url);

        await ProbeAsync(server, password, cancellationToken).ConfigureAwait(false);
        return ToDto(server);
    }

    /// <summary>
    /// Removes a friend server, along with everything imported from it.
    /// </summary>
    /// <param name="id">Local friend server identifier.</param>
    /// <response code="204">Friend server removed.</response>
    /// <response code="404">Unknown friend server.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteFriendServer([FromRoute] Guid id)
    {
        var removed = ConfigurationStore.Update(config =>
        {
            var found = Array.Find(config.FriendServers, s => s.Id == id);
            if (found is null)
            {
                return null;
            }

            config.FriendServers = config.FriendServers.Where(s => s.Id != id).ToArray();
            return found;
        });

        if (removed is null)
        {
            return NotFound();
        }

        // the folders go away, so the libraries must stop pointing at them
        var detached = _attacher.Detach(removed);

        var discarded = _cleaner.RemoveAllForServer(id);
        _cleaner.RemoveServerFolder(removed);
        if (discarded > 0 || detached)
        {
            _libraryManager.QueueLibraryScan();
        }

        _logger.LogInformation("[ShadowLibrary] Removed friend server {Id} and its {Count} imported items.", id, discarded);
        return NoContent();
    }

    /// <summary>
    /// Reports the address the generated .strm files point at, and fills it in from the
    /// current connection when the server offers nothing better.
    /// </summary>
    /// <response code="200">Result returned.</response>
    /// <returns>The address in use and where it comes from.</returns>
    /// <remarks>
    /// A player can be handed the address of a .strm directly, so it has to be reachable from
    /// outside this machine. The address an administrator reaches the dashboard through is the
    /// best evidence of that available without asking, which is why it is adopted here rather
    /// than left as a suggestion. It is written once. Clearing the field afterwards is a
    /// deliberate choice and is not undone on the next visit.
    /// </remarks>
    [HttpPost("StreamBaseUrl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StreamBaseUrlResult> DetectStreamBaseUrl()
    {
        var adopted = AdoptRequestUrl();
        var (url, source) = _writer.ResolveBaseUrl();

        return new StreamBaseUrlResult
        {
            Effective = url,
            Source = adopted ? "request" : source,
            Adopted = adopted
        };
    }

    /// <summary>
    /// Writes the address of the current request into the configuration, when nothing else
    /// says where this server is reachable.
    /// </summary>
    /// <returns>True when the address was adopted.</returns>
    private bool AdoptRequestUrl()
    {
        var configuration = ConfigurationStore.Current;
        if (configuration.StreamBaseUrlDetected
            || FriendServerClient.NormalizeUrl(configuration.LocalApiUrl) is not null)
        {
            return false;
        }

        // an address the server publishes itself is a deliberate setting and beats the one
        // this administrator happens to be using
        if (_writer.ResolvePublishedUrl() is not null || _writer.ResolveStartupPublishedUrl() is not null)
        {
            return false;
        }

        var candidate = BuildRequestUrl();
        if (candidate is null)
        {
            return false;
        }

        ConfigurationStore.Update(config =>
        {
            config.LocalApiUrl = candidate;
            config.StreamBaseUrlDetected = true;
        });

        _logger.LogInformation(
            "[ShadowLibrary] Generated files will point at {Url}, taken from the address this dashboard was reached through.",
            candidate);

        return true;
    }

    /// <summary>
    /// Builds the address the caller reached this server through, base path included.
    /// </summary>
    /// <returns>The address, or null when it could not serve a player.</returns>
    private string? BuildRequestUrl()
    {
        var host = Request.Host;
        if (!host.HasValue || string.IsNullOrEmpty(host.Host))
        {
            return null;
        }

        var scheme = MediaFileWriter.ResolveAdoptedScheme(Request.Scheme, host.Host, host.Port);
        var builder = new UriBuilder(scheme, host.Host);
        if (host.Port.HasValue)
        {
            // Uri leaves out a port that is the default one of the scheme, so 443 under https
            // disappears on its own
            builder.Port = host.Port.Value;
        }

        var basePath = Request.PathBase.Value;
        if (!string.IsNullOrEmpty(basePath))
        {
            builder.Path = basePath;
        }

        var url = FriendServerClient.NormalizeUrl(builder.Uri.ToString());

        // an administrator sitting on the machine itself would otherwise pin every generated
        // file to an address no other device can reach
        return url is not null && MediaFileWriter.IsRoutableHost(url) ? url : null;
    }

    /// <summary>
    /// Tests a friend server connection: reachability, authentication, visible libraries and mode.
    /// </summary>
    /// <param name="request">Connection settings to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Test ran. The result says whether it succeeded.</response>
    /// <response code="400">No usable password.</response>
    /// <returns>The test result.</returns>
    [HttpPost("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConnectionTestResult>> TestConnection(
        [FromBody] ConnectionTestRequest request,
        CancellationToken cancellationToken)
    {
        var saved = request.Id is null
            ? null
            : Array.Find(ConfigurationStore.Current.FriendServers, s => s.Id == request.Id.Value);

        var password = string.IsNullOrEmpty(request.Password)
            ? _secretStore.Unprotect(saved?.EncryptedPassword)
            : request.Password;

        if (string.IsNullOrEmpty(password))
        {
            return BadRequest("No password given and none stored for this friend server.");
        }

        var deviceId = saved is null
            ? FriendServerClient.BuildDeviceId(request.Url, request.Username)
            : FriendServerClient.BuildDeviceId(saved.Id);

        var result = await _client.TestConnectionAsync(
            request.Url,
            request.Username.Trim(),
            password,
            deviceId,
            cancellationToken).ConfigureAwait(false);

        if (result.Success && saved is not null)
        {
            ConfigurationStore.Update(_ =>
            {
                saved.LastKnownMode = result.Mode;
                saved.LastContactUtc = DateTime.UtcNow;
            });
        }

        return result;
    }

    /// <summary>
    /// Contacts a freshly saved friend server to record its mode and contact time.
    /// A failure is logged and left as is, saving already succeeded.
    /// </summary>
    private async Task ProbeAsync(FriendServer server, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ProbeBudget);

        ConnectionTestResult result;
        try
        {
            result = await _client.TestConnectionAsync(
                server.Url,
                server.Username,
                password,
                FriendServerClient.BuildDeviceId(server.Id),
                budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[ShadowLibrary] Mode detection for {Name} ran out of time.", server.Name);
            return;
        }

        if (!result.Success)
        {
            _logger.LogWarning("[ShadowLibrary] Mode detection for {Name} failed. {Message}", server.Name, result.Message);
            return;
        }

        ConfigurationStore.Update(_ =>
        {
            server.LastKnownMode = result.Mode;
            server.LastContactUtc = DateTime.UtcNow;
        });
    }

    private static FriendServerDto ToDto(FriendServer server) => new()
    {
        Id = server.Id,
        Name = server.Name,
        Url = server.Url,
        Username = server.Username,
        HasPassword = !string.IsNullOrEmpty(server.EncryptedPassword),
        Enabled = server.Enabled,
        SyncAllLibraries = server.SyncAllLibraries,
        LibraryIds = server.LibraryIds,
        MovieLibraryName = server.MovieLibraryName,
        ShowLibraryName = server.ShowLibraryName,
        LastKnownMode = server.LastKnownMode,
        LastContactUtc = server.LastContactUtc
    };
}
