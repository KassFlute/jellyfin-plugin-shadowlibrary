using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Hands out session tokens for friend servers, reusing the stored one and
/// re-authenticating when it is missing or rejected.
/// </summary>
public class FriendServerSessionProvider
{
    private readonly FriendServerClient _client;
    private readonly SecretStore _secretStore;
    private readonly ILogger<FriendServerSessionProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendServerSessionProvider"/> class.
    /// </summary>
    /// <param name="client">Friend server client.</param>
    /// <param name="secretStore">Credential encryption.</param>
    /// <param name="logger">Logger.</param>
    public FriendServerSessionProvider(
        FriendServerClient client,
        SecretStore secretStore,
        ILogger<FriendServerSessionProvider> logger)
    {
        _client = client;
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <summary>
    /// Returns a usable session, authenticating when needed.
    /// </summary>
    /// <param name="server">Friend server.</param>
    /// <param name="forceRefresh">Ignore the stored token and authenticate again.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session, or null when the friend server could not be reached or refused the account.</returns>
    public async Task<FriendServerSession?> GetAsync(
        FriendServer server,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var url = FriendServerClient.NormalizeUrl(server.Url);
        if (url is null)
        {
            _logger.LogWarning("[ShadowLibrary] Friend server {Name} has an unusable URL.", server.Name);
            return null;
        }

        var deviceId = FriendServerClient.BuildDeviceId(server.Id);

        if (!forceRefresh)
        {
            var storedToken = _secretStore.Unprotect(server.EncryptedAccessToken);
            if (!string.IsNullOrEmpty(storedToken) && !string.IsNullOrEmpty(server.RemoteUserId))
            {
                return new FriendServerSession
                {
                    Url = url,
                    AccessToken = storedToken,
                    RemoteUserId = server.RemoteUserId,
                    DeviceId = deviceId
                };
            }
        }

        var password = _secretStore.Unprotect(server.EncryptedPassword);
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("[ShadowLibrary] No usable password stored for friend server {Name}.", server.Name);
            return null;
        }

        try
        {
            var auth = await _client
                .AuthenticateAsync(url, server.Username, password, deviceId, cancellationToken)
                .ConfigureAwait(false);

            if (auth?.AccessToken is null || string.IsNullOrEmpty(auth.User?.Id))
            {
                _logger.LogWarning("[ShadowLibrary] Friend server {Name} returned no session.", server.Name);
                return null;
            }

            var encrypted = _secretStore.Protect(auth.AccessToken);
            ConfigurationStore.Update(config =>
            {
                var stored = Array.Find(config.FriendServers, s => s.Id == server.Id);
                if (stored is not null)
                {
                    stored.EncryptedAccessToken = encrypted;
                    stored.RemoteUserId = auth.User.Id;
                }
            });

            server.EncryptedAccessToken = encrypted;
            server.RemoteUserId = auth.User.Id;

            return new FriendServerSession
            {
                Url = url,
                AccessToken = auth.AccessToken,
                RemoteUserId = auth.User.Id,
                DeviceId = deviceId
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Could not authenticate against friend server {Name}.", server.Name);
            return null;
        }
    }
}
