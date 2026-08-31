using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Security;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using Jellyfin.Plugin.ShadowLibrary.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ShadowLibrary;

/// <summary>
/// Registers plugin services in the server container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(
            FriendServerClient.StreamClientName,
            client => client.Timeout = Timeout.InfiniteTimeSpan);

        serviceCollection.AddSingleton<SecretStore>();
        serviceCollection.AddSingleton<FriendServerClient>();
        serviceCollection.AddSingleton<ImportedItemStore>();
        serviceCollection.AddSingleton<FriendServerSessionProvider>();
        serviceCollection.AddSingleton<MediaFileWriter>();
        serviceCollection.AddSingleton<ImportedMediaCleaner>();
        serviceCollection.AddSingleton<MediaProbe>();
        serviceCollection.AddSingleton<LibraryAttacher>();
        serviceCollection.AddSingleton<GeneratedPathMigrator>();
        serviceCollection.AddSingleton<FriendServerSynchronizer>();
        serviceCollection.AddSingleton<IScheduledTask, SyncScheduledTask>();
    }
}
