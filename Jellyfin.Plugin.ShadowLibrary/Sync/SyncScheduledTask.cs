using Jellyfin.Plugin.ShadowLibrary.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Scheduled task that syncs every enabled friend server. The cadence is the task
/// trigger, so running it by hand from the dashboard syncs right away.
/// </summary>
public class SyncScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly FriendServerSynchronizer _synchronizer;
    private readonly ILogger<SyncScheduledTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncScheduledTask"/> class.
    /// </summary>
    /// <param name="synchronizer">Per server synchroniser.</param>
    /// <param name="logger">Logger.</param>
    public SyncScheduledTask(FriendServerSynchronizer synchronizer, ILogger<SyncScheduledTask> logger)
    {
        _synchronizer = synchronizer;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Synchronise friend servers";

    /// <inheritdoc />
    public string Key => "ShadowLibrarySync";

    /// <inheritdoc />
    public string Description =>
        "Imports movies and shows from the configured friend servers as .strm entries and removes the ones that are gone.";

    /// <inheritdoc />
    public string Category => "ShadowLibrary";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var servers = ConfigurationStore.Current.FriendServers.Where(s => s.Enabled).ToArray();
        if (servers.Length == 0)
        {
            _logger.LogInformation("[ShadowLibrary] No enabled friend server to synchronise.");
            progress.Report(100);
            return;
        }

        var local = _synchronizer.BuildLocalCatalogue();

        for (var i = 0; i < servers.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(i * 100d / servers.Length);

            try
            {
                await _synchronizer.SyncAsync(servers[i], local, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ShadowLibrary] Synchronisation of {Name} failed.", servers[i].Name);
            }
        }

        progress.Report(100);
    }
}
