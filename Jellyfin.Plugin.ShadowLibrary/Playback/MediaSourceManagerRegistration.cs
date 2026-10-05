using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Playback;

/// <summary>
/// Puts <see cref="ShadowMediaSourceManager"/> in front of Jellyfin's media source manager.
/// </summary>
/// <remarks>
/// Plugins register after the server, so the last IMediaSourceManager registration wins.
/// The wrapped instance comes from the server's own registration, so no server assembly is
/// referenced. Anything but the stock registration is left alone: another plugin may have
/// wrapped it already, and stacking blindly could drop its behaviour.
/// </remarks>
public static class MediaSourceManagerRegistration
{
    private const string StockImplementation = "Emby.Server.Implementations.Library.MediaSourceManager";

    /// <summary>
    /// Registers the wrapper, or a startup warning saying why it was not.
    /// </summary>
    /// <param name="services">Server service collection.</param>
    public static void Register(IServiceCollection services)
    {
        var current = services.LastOrDefault(d => d.ServiceType == typeof(IMediaSourceManager));
        var stock = current?.ImplementationType;

        if (stock?.FullName != StockImplementation || current!.Lifetime != ServiceLifetime.Singleton)
        {
            var found = stock?.FullName ?? current?.ImplementationFactory?.Method.DeclaringType?.FullName ?? "nothing";
            services.AddHostedService(sp => new SkippedNotice(found, sp.GetRequiredService<ILogger<SkippedNotice>>()));
            return;
        }

        services.AddSingleton(stock);
        services.AddSingleton<IMediaSourceManager>(sp => new ShadowMediaSourceManager(
            (IMediaSourceManager)sp.GetRequiredService(stock),
            sp.GetService<IHttpContextAccessor>(),
            sp.GetRequiredService<ILogger<ShadowMediaSourceManager>>()));
    }

    /// <summary>
    /// Logs once at startup that playback runs without the wrapper.
    /// </summary>
    private sealed class SkippedNotice(string found, ILogger<SkippedNotice> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogWarning(
                "[ShadowLibrary] IMediaSourceManager is provided by {Found}, not by Jellyfin itself. Imported items keep the slow playback start.",
                found);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
