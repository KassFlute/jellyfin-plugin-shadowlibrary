using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Writes the .strm, .nfo and cached images of imported movies, series and episodes.
/// </summary>
public class MediaFileWriter
{
    /// <summary>
    /// Subfolder holding the imported movies of a friend server.
    /// </summary>
    public const string MoviesFolderName = "Movies";

    /// <summary>
    /// Subfolder holding the imported series of a friend server.
    /// </summary>
    public const string ShowsFolderName = "Shows";

    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();

    /// <summary>
    /// An address outside every private range, used to ask the server what it would tell a
    /// caller from the internet. Taken from the documentation range of RFC 5737, so it can
    /// never match a real network of the host.
    /// </summary>
    private static readonly IPAddress ExternalProbe = IPAddress.Parse("203.0.113.1");

    private readonly FriendServerClient _client;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IServerConfigurationManager _serverConfiguration;
    private readonly ILogger<MediaFileWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaFileWriter"/> class.
    /// </summary>
    /// <param name="client">Friend server client.</param>
    /// <param name="applicationHost">Local server, used to build the proxy URL.</param>
    /// <param name="serverConfiguration">Server configuration, read for the published address.</param>
    /// <param name="logger">Logger.</param>
    public MediaFileWriter(
        FriendServerClient client,
        IServerApplicationHost applicationHost,
        IServerConfigurationManager serverConfiguration,
        ILogger<MediaFileWriter> logger)
    {
        _client = client;
        _applicationHost = applicationHost;
        _serverConfiguration = serverConfiguration;
        _logger = logger;
    }

    /// <summary>
    /// Builds the folder a friend server writes into.
    /// </summary>
    /// <param name="rootPath">Configured media root.</param>
    /// <param name="server">Friend server.</param>
    /// <returns>The friend server folder.</returns>
    public static string BuildServerFolder(string rootPath, FriendServer server)
        => Path.Combine(rootPath, BuildFolderName(server));

    /// <summary>
    /// Builds the folder name of a friend server, one that never changes when the entry is
    /// renamed. Falls back to the display name for entries created before the name was frozen.
    /// </summary>
    /// <param name="server">Friend server.</param>
    /// <returns>The folder name.</returns>
    public static string BuildFolderName(FriendServer server)
    {
        if (!string.IsNullOrEmpty(server.FolderName))
        {
            return server.FolderName;
        }

        var name = Sanitize(server.Name);
        return name.Length == 0 ? server.Id.ToString("N", CultureInfo.InvariantCulture) : name;
    }

    /// <summary>
    /// Picks the folder name of a new friend server, keeping it readable and free of any
    /// collision with the entries already configured.
    /// </summary>
    /// <param name="name">Display name of the new entry.</param>
    /// <param name="id">Identifier of the new entry.</param>
    /// <param name="taken">Folder names already in use.</param>
    /// <returns>A folder name to freeze on the entry.</returns>
    public static string ReserveFolderName(string name, Guid id, IEnumerable<string> taken)
    {
        var cleaned = Sanitize(name);
        if (cleaned.Length == 0)
        {
            return id.ToString("N", CultureInfo.InvariantCulture);
        }

        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(cleaned))
        {
            return cleaned;
        }

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", cleaned, suffix);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return id.ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the tag that marks where an imported item comes from.
    /// </summary>
    /// <param name="server">Friend server.</param>
    /// <returns>The origin tag.</returns>
    public static string BuildOriginTag(FriendServer server)
        => string.Format(CultureInfo.InvariantCulture, "ShadowLibrary: {0} ({1})", server.Name, server.Url);

    /// <summary>
    /// Builds the folder name of a movie or a series, in the layout Jellyfin expects.
    /// </summary>
    /// <param name="title">Title.</param>
    /// <param name="year">Release year, when known.</param>
    /// <returns>A folder name safe on every supported platform.</returns>
    public static string BuildTitleFolderName(string title, int? year)
    {
        var cleaned = Sanitize(title);
        if (cleaned.Length == 0)
        {
            cleaned = "Untitled";
        }

        return year is null
            ? cleaned
            : string.Format(CultureInfo.InvariantCulture, "{0} ({1})", cleaned, year.Value);
    }

    /// <summary>
    /// Builds the season folder name of an episode.
    /// </summary>
    /// <param name="seasonNumber">Season number.</param>
    /// <returns>The folder name.</returns>
    public static string BuildSeasonFolderName(int seasonNumber)
        => string.Format(CultureInfo.InvariantCulture, "Season {0:00}", seasonNumber);

    /// <summary>
    /// Builds the file base name of an episode, in a form Jellyfin can parse back.
    /// </summary>
    /// <param name="episode">Remote episode.</param>
    /// <returns>The base name, without extension.</returns>
    public static string BuildEpisodeBaseName(RemoteEpisode episode)
    {
        var code = string.Format(
            CultureInfo.InvariantCulture,
            "S{0:00}E{1:00}",
            episode.ParentIndexNumber ?? 0,
            episode.IndexNumber ?? 0);

        var title = Sanitize(episode.Name);
        return title.Length == 0 ? code : code + " - " + title;
    }

    /// <summary>
    /// Hashes the metadata that ends up in a movie .nfo.
    /// </summary>
    /// <param name="movie">Remote movie.</param>
    /// <param name="originTag">Origin tag written in the .nfo.</param>
    /// <returns>A hex hash.</returns>
    public static string ComputeHash(RemoteMovie movie, string originTag)
    {
        var builder = new StringBuilder();
        builder.Append(originTag).Append('\n')
            .Append(movie.Name).Append('\n')
            .Append(movie.OriginalTitle).Append('\n')
            .Append(movie.SortName).Append('\n')
            .Append(movie.ProductionYear?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(movie.PremiereDate?.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
            .Append(movie.Overview).Append('\n')
            .Append(movie.OfficialRating).Append('\n')
            .Append(movie.CommunityRating?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(movie.RunTimeTicks?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .AppendJoin(',', movie.Genres ?? []).Append('\n')
            .AppendJoin(',', movie.Taglines ?? []).Append('\n')
            .AppendJoin(',', (movie.Studios ?? []).Select(s => s.Name)).Append('\n');

        AppendShared(builder, movie.People, movie.ProviderIds, movie.ImageTags);
        return Hash(builder);
    }

    /// <summary>
    /// Hashes the metadata that ends up in a tvshow .nfo.
    /// </summary>
    /// <param name="series">Remote series.</param>
    /// <param name="originTag">Origin tag written in the .nfo.</param>
    /// <returns>A hex hash.</returns>
    public static string ComputeHash(RemoteSeries series, string originTag)
    {
        var builder = new StringBuilder();
        builder.Append(originTag).Append('\n')
            .Append(series.Name).Append('\n')
            .Append(series.OriginalTitle).Append('\n')
            .Append(series.SortName).Append('\n')
            .Append(series.ProductionYear?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(series.PremiereDate?.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
            .Append(series.Overview).Append('\n')
            .Append(series.OfficialRating).Append('\n')
            .Append(series.CommunityRating?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(series.Status).Append('\n')
            .AppendJoin(',', series.Genres ?? []).Append('\n')
            .AppendJoin(',', (series.Studios ?? []).Select(s => s.Name)).Append('\n');

        AppendShared(builder, series.People, series.ProviderIds, series.ImageTags);
        return Hash(builder);
    }

    /// <summary>
    /// Hashes the metadata that ends up in an episode .nfo.
    /// </summary>
    /// <param name="episode">Remote episode.</param>
    /// <returns>A hex hash.</returns>
    public static string ComputeHash(RemoteEpisode episode)
    {
        var builder = new StringBuilder();
        builder.Append(episode.Name).Append('\n')
            .Append(episode.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(episode.IndexNumber?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(episode.PremiereDate?.ToString("O", CultureInfo.InvariantCulture)).Append('\n')
            .Append(episode.Overview).Append('\n')
            .Append(episode.CommunityRating?.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(episode.RunTimeTicks?.ToString(CultureInfo.InvariantCulture)).Append('\n');

        AppendShared(builder, episode.People, episode.ProviderIds, episode.ImageTags);
        return Hash(builder);
    }

    /// <summary>
    /// Writes the files of one movie.
    /// </summary>
    /// <param name="folderPath">Folder to write into.</param>
    /// <param name="movie">Remote movie.</param>
    /// <param name="server">Friend server it comes from.</param>
    /// <param name="itemKey">Plugin side item identifier, used in the proxy URL.</param>
    /// <param name="session">Session used to fetch the images.</param>
    /// <param name="refreshImages">Fetch images again even when they are already on disk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Where the files ended up.</returns>
    public async Task<GeneratedFiles> WriteMovieAsync(
        string folderPath,
        RemoteMovie movie,
        FriendServer server,
        Guid itemKey,
        FriendServerSession session,
        bool refreshImages,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folderPath);

        var baseName = BuildTitleFolderName(movie.Name, movie.ProductionYear);
        var strmPath = Path.Combine(folderPath, baseName + ".strm");
        var nfoPath = Path.Combine(folderPath, baseName + ".nfo");

        await File.WriteAllTextAsync(strmPath, BuildStreamUrl(itemKey), cancellationToken).ConfigureAwait(false);

        await WriteNfoAsync(nfoPath, "movie", async writer =>
        {
            await WriteIfSetAsync(writer, "title", movie.Name).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "originaltitle", movie.OriginalTitle).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "sorttitle", movie.SortName).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "plot", movie.Overview).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "mpaa", movie.OfficialRating).ConfigureAwait(false);
            await WriteNumberAsync(writer, "year", movie.ProductionYear).ConfigureAwait(false);
            await WriteDateAsync(writer, "premiered", movie.PremiereDate).ConfigureAwait(false);
            await WriteRatingAsync(writer, movie.CommunityRating).ConfigureAwait(false);
            await WriteRuntimeAsync(writer, movie.RunTimeTicks).ConfigureAwait(false);

            foreach (var tagline in movie.Taglines ?? [])
            {
                await WriteIfSetAsync(writer, "tagline", tagline).ConfigureAwait(false);
            }

            foreach (var genre in movie.Genres ?? [])
            {
                await WriteIfSetAsync(writer, "genre", genre).ConfigureAwait(false);
            }

            foreach (var studio in movie.Studios ?? [])
            {
                await WriteIfSetAsync(writer, "studio", studio.Name).ConfigureAwait(false);
            }

            await WriteOriginTagAsync(writer, server).ConfigureAwait(false);
            await WriteProviderIdsAsync(writer, movie.ProviderIds).ConfigureAwait(false);
            await WritePeopleAsync(writer, movie.People).ConfigureAwait(false);
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        await WriteImagesAsync(
            folderPath,
            movie.Id,
            [("Primary", "poster.jpg"), ("Logo", "logo.png")],
            movie.BackdropImageTags?.Length > 0,
            movie.ImageTags,
            session,
            refreshImages,
            cancellationToken).ConfigureAwait(false);

        return new GeneratedFiles(folderPath, strmPath, nfoPath);
    }

    /// <summary>
    /// Writes the show level files of a series, shared by all its episodes.
    /// </summary>
    /// <param name="folderPath">Series folder.</param>
    /// <param name="series">Remote series.</param>
    /// <param name="server">Friend server it comes from.</param>
    /// <param name="session">Session used to fetch the images.</param>
    /// <param name="refreshImages">Fetch images again even when they are already on disk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task WriteSeriesAsync(
        string folderPath,
        RemoteSeries series,
        FriendServer server,
        FriendServerSession session,
        bool refreshImages,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folderPath);

        await WriteNfoAsync(Path.Combine(folderPath, "tvshow.nfo"), "tvshow", async writer =>
        {
            await WriteIfSetAsync(writer, "title", series.Name).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "originaltitle", series.OriginalTitle).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "sorttitle", series.SortName).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "plot", series.Overview).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "mpaa", series.OfficialRating).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "status", series.Status).ConfigureAwait(false);
            await WriteNumberAsync(writer, "year", series.ProductionYear).ConfigureAwait(false);
            await WriteDateAsync(writer, "premiered", series.PremiereDate).ConfigureAwait(false);
            await WriteRatingAsync(writer, series.CommunityRating).ConfigureAwait(false);

            foreach (var genre in series.Genres ?? [])
            {
                await WriteIfSetAsync(writer, "genre", genre).ConfigureAwait(false);
            }

            foreach (var studio in series.Studios ?? [])
            {
                await WriteIfSetAsync(writer, "studio", studio.Name).ConfigureAwait(false);
            }

            await WriteOriginTagAsync(writer, server).ConfigureAwait(false);
            await WriteProviderIdsAsync(writer, series.ProviderIds).ConfigureAwait(false);
            await WritePeopleAsync(writer, series.People).ConfigureAwait(false);
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        await WriteImagesAsync(
            folderPath,
            series.Id,
            [("Primary", "poster.jpg"), ("Logo", "logo.png")],
            series.BackdropImageTags?.Length > 0,
            series.ImageTags,
            session,
            refreshImages,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the files of one episode.
    /// </summary>
    /// <param name="seasonFolderPath">Season folder, shared with the other episodes.</param>
    /// <param name="episode">Remote episode.</param>
    /// <param name="itemKey">Plugin side item identifier, used in the proxy URL.</param>
    /// <param name="session">Session used to fetch the images.</param>
    /// <param name="refreshImages">Fetch images again even when they are already on disk.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Where the files ended up.</returns>
    public async Task<GeneratedFiles> WriteEpisodeAsync(
        string seasonFolderPath,
        RemoteEpisode episode,
        Guid itemKey,
        FriendServerSession session,
        bool refreshImages,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(seasonFolderPath);

        var baseName = BuildEpisodeBaseName(episode);
        var strmPath = Path.Combine(seasonFolderPath, baseName + ".strm");
        var nfoPath = Path.Combine(seasonFolderPath, baseName + ".nfo");

        await File.WriteAllTextAsync(strmPath, BuildStreamUrl(itemKey), cancellationToken).ConfigureAwait(false);

        await WriteNfoAsync(nfoPath, "episodedetails", async writer =>
        {
            await WriteIfSetAsync(writer, "title", episode.Name).ConfigureAwait(false);
            await WriteNumberAsync(writer, "season", episode.ParentIndexNumber).ConfigureAwait(false);
            await WriteNumberAsync(writer, "episode", episode.IndexNumber).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "plot", episode.Overview).ConfigureAwait(false);
            await WriteDateAsync(writer, "aired", episode.PremiereDate).ConfigureAwait(false);
            await WriteRatingAsync(writer, episode.CommunityRating).ConfigureAwait(false);
            await WriteRuntimeAsync(writer, episode.RunTimeTicks).ConfigureAwait(false);
            await WriteProviderIdsAsync(writer, episode.ProviderIds).ConfigureAwait(false);
            await WritePeopleAsync(writer, episode.People).ConfigureAwait(false);
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Jellyfin picks up an episode still from a file named after the episode itself
        await WriteImagesAsync(
            seasonFolderPath,
            episode.Id,
            [("Primary", baseName + "-thumb.jpg")],
            false,
            episode.ImageTags,
            session,
            refreshImages,
            cancellationToken).ConfigureAwait(false);

        return new GeneratedFiles(seasonFolderPath, strmPath, nfoPath);
    }

    /// <summary>
    /// Rewrites a .strm whose URL no longer matches the current configuration. The URL depends
    /// on local settings, not on the friend metadata, so it is checked on every cycle.
    /// </summary>
    /// <param name="strmPath">Path of the .strm file.</param>
    /// <param name="itemKey">Plugin side item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the file was rewritten.</returns>
    public async Task<bool> EnsureStreamUrlAsync(
        string strmPath,
        Guid itemKey,
        CancellationToken cancellationToken)
    {
        var expected = BuildStreamUrl(itemKey);

        try
        {
            var current = await File.ReadAllTextAsync(strmPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.Trim(), expected, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[ShadowLibrary] Could not read {Path}, rewriting it.", strmPath);
        }

        await File.WriteAllTextAsync(strmPath, expected, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Builds the local proxy URL a .strm points at.
    /// </summary>
    /// <param name="itemKey">Plugin side item identifier.</param>
    /// <returns>The absolute URL.</returns>
    public string BuildStreamUrl(Guid itemKey)
    {
        var baseUrl = ResolveBaseUrl().Url.TrimEnd('/');

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}/ShadowLibrary/stream/{1}?key={2}",
            baseUrl,
            itemKey.ToString("N", CultureInfo.InvariantCulture),
            Uri.EscapeDataString(GetOrCreateStreamKey()));
    }

    /// <summary>
    /// Resolves the address the generated .strm files point at.
    /// </summary>
    /// <returns>The base URL and where it came from.</returns>
    /// <remarks>
    /// Playback of a remote source can be handed straight to the player, so this address has
    /// to be reachable by players and not only by this server. Explicit settings win, then
    /// what Jellyfin was told to publish, then the guess from the network interfaces.
    /// </remarks>
    public (string Url, string Source) ResolveBaseUrl()
    {
        var configured = FriendServerClient.NormalizeUrl(ConfigurationStore.Current.LocalApiUrl);
        if (configured is not null)
        {
            return (configured, "override");
        }

        var published = ResolvePublishedUrl();
        if (published is not null)
        {
            return (published, "published");
        }

        var startup = ResolveStartupPublishedUrl();
        if (startup is not null)
        {
            return (startup, "startup");
        }

        return (_applicationHost.GetApiUrlForLocalAccess(allowHttps: false), "local");
    }

    /// <summary>
    /// Reads the address the server was started with, the published server URL of the command
    /// line or of the container environment. It is private to the host, and asking for the
    /// address of an external caller is the only way it surfaces.
    /// </summary>
    /// <returns>The published address, or null when the server was not given one.</returns>
    public string? ResolveStartupPublishedUrl()
    {
        string? answer;
        try
        {
            answer = _applicationHost.GetSmartApiUrl(ExternalProbe);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException)
        {
            _logger.LogDebug(ex, "[ShadowLibrary] Could not ask the server for its published address.");
            return null;
        }

        var url = FriendServerClient.NormalizeUrl(answer);
        if (url is null)
        {
            return null;
        }

        // without a published address the call falls back to an interface of the host, which
        // is what the last resort already returns, so only a routable answer is worth taking
        return IsRoutableHost(url) ? url : null;
    }

    /// <summary>
    /// Picks the scheme of an address adopted from an administrator connection.
    /// </summary>
    /// <param name="observedScheme">Scheme the request arrived with.</param>
    /// <param name="host">Host the request arrived on.</param>
    /// <param name="port">Port the request carried, null when it was the default one.</param>
    /// <returns>The scheme to write.</returns>
    /// <remarks>
    /// A proxy that terminates TLS and is not declared in the KnownProxies of Jellyfin leaves
    /// the request looking like plain http, so a name served on the default port is taken to
    /// be https. An address, or a name on a port of its own, is a direct connection and is
    /// left alone, otherwise a plain http://nas.local:8096 would be broken to fix a guess.
    /// </remarks>
    public static string ResolveAdoptedScheme(string observedScheme, string host, int? port)
    {
        if (string.Equals(observedScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UriSchemeHttps;
        }

        var isName = !IPAddress.TryParse(host, out _);
        var isDefaultPort = port is null or 443;

        return isName && isDefaultPort ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
    }

    /// <summary>
    /// Tells whether an address could be reached by a player that is not on this machine.
    /// A name is assumed to be routable, an address is checked against the private ranges.
    /// </summary>
    /// <param name="url">Absolute URL to look at.</param>
    /// <returns>True when the host is a name or a public address.</returns>
    public static bool IsRoutableHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return true;
        }

        return bytes[0] switch
        {
            10 => false,
            127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => false,
            192 when bytes[1] == 168 => false,
            _ => true
        };
    }

    /// <summary>
    /// Reads the address Jellyfin is configured to advertise for remote access. Entries are
    /// "subnet=url" pairs, and only the ones that apply to every caller are usable here,
    /// since a .strm carries one URL for all of them.
    /// </summary>
    /// <returns>The published URL, or null when none applies.</returns>
    public string? ResolvePublishedUrl()
    {
        string[] overrides;
        try
        {
            overrides = _serverConfiguration.GetNetworkConfiguration().PublishedServerUriBySubnet;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "[ShadowLibrary] Could not read the network configuration.");
            return null;
        }

        foreach (var scope in new[] { "all", "external" })
        {
            foreach (var entry in overrides ?? [])
            {
                var separator = entry.IndexOf('=', StringComparison.Ordinal);
                if (separator < 0
                    || !string.Equals(entry[..separator].Trim(), scope, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = FriendServerClient.NormalizeUrl(entry[(separator + 1)..]);
                if (url is not null)
                {
                    return url;
                }
            }
        }

        return null;
    }

    private static void AppendShared(
        StringBuilder builder,
        RemotePerson[]? people,
        Dictionary<string, string>? providerIds,
        Dictionary<string, string>? imageTags)
    {
        builder
            .AppendJoin(',', (people ?? []).Select(p => p.Type + ':' + p.Name + ':' + p.Role)).Append('\n')
            .AppendJoin(',', (providerIds ?? []).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + '=' + p.Value)).Append('\n')
            .AppendJoin(',', (imageTags ?? []).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + '=' + p.Value));
    }

    private static string Hash(StringBuilder builder)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));

    private static string GetOrCreateStreamKey()
    {
        var existing = ConfigurationStore.Current.StreamAccessKey;
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        return ConfigurationStore.Update(config =>
        {
            if (string.IsNullOrEmpty(config.StreamAccessKey))
            {
                config.StreamAccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            }

            return config.StreamAccessKey;
        });
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Trim().Select(c => InvalidNameChars.Contains(c) ? ' ' : c).ToArray());
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return cleaned.Trim().TrimEnd('.');
    }

    private static async Task WriteNfoAsync(
        string nfoPath,
        string rootElement,
        Func<XmlWriter, Task> writeBody)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false),
            Async = true
        };

        var stream = new FileStream(nfoPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            var writer = XmlWriter.Create(stream, settings);
            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteStartDocumentAsync().ConfigureAwait(false);
                await writer.WriteStartElementAsync(null, rootElement, null).ConfigureAwait(false);
                await writeBody(writer).ConfigureAwait(false);
                await writer.WriteEndElementAsync().ConfigureAwait(false);
                await writer.WriteEndDocumentAsync().ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteIfSetAsync(XmlWriter writer, string element, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            await writer.WriteElementStringAsync(null, element, null, value).ConfigureAwait(false);
        }
    }

    private static async Task WriteNumberAsync(XmlWriter writer, string element, int? value)
    {
        if (value is not null)
        {
            await writer.WriteElementStringAsync(
                null, element, null, value.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }

    private static async Task WriteDateAsync(XmlWriter writer, string element, DateTime? value)
    {
        if (value is not null)
        {
            await writer.WriteElementStringAsync(
                null, element, null, value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteRatingAsync(XmlWriter writer, float? rating)
    {
        if (rating is not null)
        {
            await writer.WriteElementStringAsync(
                null, "rating", null, rating.Value.ToString("0.0", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteRuntimeAsync(XmlWriter writer, long? runTimeTicks)
    {
        if (runTimeTicks is not null)
        {
            var minutes = (int)TimeSpan.FromTicks(runTimeTicks.Value).TotalMinutes;
            await writer.WriteElementStringAsync(
                null, "runtime", null, minutes.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }

    private static async Task WriteOriginTagAsync(XmlWriter writer, FriendServer server)
    {
        // the origin tag, so the item can be filtered natively in the interface
        await writer.WriteElementStringAsync(null, "tag", null, BuildOriginTag(server)).ConfigureAwait(false);
    }

    private static async Task WriteProviderIdsAsync(XmlWriter writer, Dictionary<string, string>? providerIds)
    {
        foreach (var (provider, value) in providerIds ?? [])
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            await writer.WriteStartElementAsync(null, "uniqueid", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "type", null, provider.ToLowerInvariant())
                .ConfigureAwait(false);
            await writer.WriteStringAsync(value).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }
    }

    private static async Task WritePeopleAsync(XmlWriter writer, RemotePerson[]? people)
    {
        foreach (var person in people ?? [])
        {
            if (string.IsNullOrEmpty(person.Name))
            {
                continue;
            }

            if (string.Equals(person.Type, "Director", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteElementStringAsync(null, "director", null, person.Name).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(person.Type, "Writer", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteElementStringAsync(null, "credits", null, person.Name).ConfigureAwait(false);
                continue;
            }

            await writer.WriteStartElementAsync(null, "actor", null).ConfigureAwait(false);
            await writer.WriteElementStringAsync(null, "name", null, person.Name).ConfigureAwait(false);
            await WriteIfSetAsync(writer, "role", person.Role).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteImagesAsync(
        string folderPath,
        string remoteItemId,
        (string ImageType, string FileName)[] tagged,
        bool hasBackdrop,
        Dictionary<string, string>? imageTags,
        FriendServerSession session,
        bool refreshImages,
        CancellationToken cancellationToken)
    {
        var wanted = tagged.Where(w => imageTags?.ContainsKey(w.ImageType) == true).ToList();
        if (hasBackdrop)
        {
            wanted.Add(("Backdrop", "fanart.jpg"));
        }

        foreach (var (imageType, fileName) in wanted)
        {
            var destination = Path.Combine(folderPath, fileName);
            if (!refreshImages && File.Exists(destination))
            {
                continue;
            }

            var written = await _client.DownloadImageAsync(
                session.Url,
                session.AccessToken,
                session.DeviceId,
                remoteItemId,
                imageType,
                destination,
                cancellationToken).ConfigureAwait(false);

            if (!written)
            {
                _logger.LogDebug("[ShadowLibrary] No {Type} image for {ItemId}.", imageType, remoteItemId);
            }
        }
    }
}
