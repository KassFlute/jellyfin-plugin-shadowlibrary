using System.Globalization;
using System.Net.Mime;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ShadowLibrary.Api;

/// <summary>
/// Endpoints other ShadowLibrary instances call on this server.
/// </summary>
[ApiController]
[Authorize]
[Route("ShadowLibrary")]
[Produces(MediaTypeNames.Application.Json)]
public class ShadowLibraryController : ControllerBase
{
    private const int MaxPageSize = 1000;

    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ImportedItemStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowLibraryController"/> class.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="authorizationContext">Authorization context of the caller.</param>
    /// <param name="store">Imported item store.</param>
    public ShadowLibraryController(
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        ImportedItemStore store)
    {
        _libraryManager = libraryManager;
        _authorizationContext = authorizationContext;
        _store = store;
    }

    /// <summary>
    /// Confirms that the plugin runs on this server and returns its version.
    /// </summary>
    /// <response code="200">The plugin is installed and active.</response>
    /// <returns>Plugin identity and version.</returns>
    [HttpGet("ping")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PingResponse> Ping()
    {
        return new PingResponse
        {
            Plugin = "ShadowLibrary",
            Version = Plugin.Instance?.Version.ToString() ?? "unknown"
        };
    }

    /// <summary>
    /// Lists the movies and episodes this server holds natively, excluding everything
    /// ShadowLibrary imported from somewhere else. Sharing stops at one hop.
    /// </summary>
    /// <param name="startIndex">Index to start at.</param>
    /// <param name="limit">Page size.</param>
    /// <response code="200">Page returned.</response>
    /// <returns>A page of native item identifiers.</returns>
    [HttpGet("native-items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<NativeItemsResponse>> GetNativeItems(
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 200)
    {
        var imported = _store.GetLocalItemIds();

        // scoped to the caller, so a friend only ever learns about the libraries their own
        // account can see
        var authorization = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var query = new InternalItemsQuery(authorization.User)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true
        };

        // the exclusion cannot be pushed into the query, so paging happens after filtering
        var native = _libraryManager
            .GetItemList(query)
            .Where(item => !imported.Contains(item.Id))
            .Select(item => item.Id.ToString("N", CultureInfo.InvariantCulture))
            .ToArray();

        var from = Math.Clamp(startIndex, 0, native.Length);
        var size = Math.Clamp(limit, 1, MaxPageSize);

        return new NativeItemsResponse
        {
            Items = native.Skip(from).Take(size).ToArray(),
            TotalRecordCount = native.Length,
            StartIndex = from
        };
    }
}

/// <summary>
/// Response of the discovery endpoint.
/// </summary>
public class PingResponse
{
    /// <summary>
    /// Gets or sets the plugin name.
    /// </summary>
    public required string Plugin { get; set; }

    /// <summary>
    /// Gets or sets the plugin version.
    /// </summary>
    public required string Version { get; set; }
}
