using System.Net;
using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Remote;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Runs one synchronisation cycle for one friend server.
/// </summary>
public class FriendServerSynchronizer
{
    /// <summary>
    /// Key of the built in Jellyfin task that actually walks the libraries.
    /// </summary>
    private const string LibraryScanTaskKey = "RefreshLibrary";

    /// <summary>
    /// How long a cycle waits for that scan. Past it the imported items are simply left for
    /// the next cycle to resolve and inspect.
    /// </summary>
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromHours(2);

    /// <summary>
    /// How often the state of the scan is looked at once a first run has ended.
    /// </summary>
    private static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(2);

    private readonly FriendServerClient _client;
    private readonly FriendServerSessionProvider _sessions;
    private readonly ImportedItemStore _store;
    private readonly MediaFileWriter _writer;
    private readonly ImportedMediaCleaner _cleaner;
    private readonly MediaProbe _probe;
    private readonly LibraryAttacher _attacher;
    private readonly GeneratedPathMigrator _migrator;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<FriendServerSynchronizer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FriendServerSynchronizer"/> class.
    /// </summary>
    /// <param name="client">Friend server client.</param>
    /// <param name="sessions">Session provider.</param>
    /// <param name="store">Imported item store.</param>
    /// <param name="writer">File writer.</param>
    /// <param name="cleaner">Imported media cleaner.</param>
    /// <param name="probe">Media inspector.</param>
    /// <param name="attacher">Library attacher.</param>
    /// <param name="migrator">Generated path migrator.</param>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="taskManager">Server task manager, used to run and await the library scan.</param>
    /// <param name="logger">Logger.</param>
    public FriendServerSynchronizer(
        FriendServerClient client,
        FriendServerSessionProvider sessions,
        ImportedItemStore store,
        MediaFileWriter writer,
        ImportedMediaCleaner cleaner,
        MediaProbe probe,
        LibraryAttacher attacher,
        GeneratedPathMigrator migrator,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<FriendServerSynchronizer> logger)
    {
        _client = client;
        _sessions = sessions;
        _store = store;
        _writer = writer;
        _cleaner = cleaner;
        _probe = probe;
        _attacher = attacher;
        _migrator = migrator;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <summary>
    /// Reads what the local server already holds. Build it once and hand it to every friend
    /// server of the run, the answer is the same for all of them.
    /// </summary>
    /// <returns>The local catalogue.</returns>
    public LocalCatalogue BuildLocalCatalogue()
    {
        var catalogue = LocalCatalogue.Build(_libraryManager, ConfigurationStore.Current.MediaRootPath);
        catalogue.SeedClaims(_store.GetClaims());
        return catalogue;
    }

    /// <summary>
    /// Synchronises one friend server.
    /// </summary>
    /// <param name="server">Friend server.</param>
    /// <param name="local">What the local server already holds natively.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the cycle did.</returns>
    public async Task<SyncReport> SyncAsync(
        FriendServer server,
        LocalCatalogue local,
        CancellationToken cancellationToken)
    {
        var report = new SyncReport();
        var known = _store.GetByFriendServer(server.Id)
            .ToDictionary(i => i.RemoteItemId, StringComparer.Ordinal);

        var catalogue = await ListCatalogueAsync(server, cancellationToken).ConfigureAwait(false);

        if (catalogue is null)
        {
            // no listing means no proof of deletion, so every item starts or continues its countdown
            report.Reached = false;
            report.Removed = MarkUnavailable(server, known.Values, report);
            _logger.LogWarning("[ShadowLibrary] Friend server {Name} could not be listed. {Report}", server.Name, report);

            if (report.Removed > 0)
            {
                await ScanAsync(cancellationToken).ConfigureAwait(false);
            }

            return report;
        }

        report.Reached = true;

        var serverFolder = ResolveServerFolder(server);
        var moviesFolder = Path.Combine(serverFolder, MediaFileWriter.MoviesFolderName);
        var showsFolder = Path.Combine(serverFolder, MediaFileWriter.ShowsFolderName);

        // a media root that changed since the last cycle, so the files follow before anything
        // is written or attached
        var changed = _migrator.Migrate(server, serverFolder);
        if (changed)
        {
            // the rows were read before the move, they carry the old paths
            known = _store.GetByFriendServer(server.Id)
                .ToDictionary(i => i.RemoteItemId, StringComparer.Ordinal);
        }

        // done before writing anything, so the scan at the end of the cycle already covers
        // the generated folders and the user never has to declare them by hand
        var seen = new HashSet<string>(StringComparer.Ordinal);
        changed |= _attacher.Attach(server, moviesFolder, showsFolder);

        changed |= await SyncMoviesAsync(
            server,
            catalogue,
            local,
            moviesFolder,
            known,
            seen,
            report,
            cancellationToken).ConfigureAwait(false);

        changed |= await SyncSeriesAsync(
            server,
            catalogue,
            local,
            showsFolder,
            known,
            seen,
            report,
            cancellationToken).ConfigureAwait(false);

        foreach (var orphan in known.Values.Where(i => !seen.Contains(i.RemoteItemId)))
        {
            // the listing succeeded and this item is not in it, either because the friend no
            // longer holds it or because it stopped being eligible, a local copy having appeared
            _cleaner.Remove(orphan);
            report.Removed++;
            changed = true;
        }

        ConfigurationStore.Update(config =>
        {
            var stored = Array.Find(config.FriendServers, s => s.Id == server.Id);
            if (stored is not null)
            {
                stored.LastKnownMode = catalogue.Mode;
                stored.LastContactUtc = DateTime.UtcNow;
                stored.LastSyncUtc = DateTime.UtcNow;
                stored.FolderName = server.FolderName;
                stored.GeneratedFolderPath = server.GeneratedFolderPath;
                stored.AttachedMovieLibraryName = server.AttachedMovieLibraryName;
                stored.AttachedShowLibraryName = server.AttachedShowLibraryName;
                stored.AttachedMoviePath = server.AttachedMoviePath;
                stored.AttachedShowPath = server.AttachedShowPath;
            }
        });

        if (changed)
        {
            // awaited rather than queued, so the new items exist before the inspection below
            // and their tracks are known at the end of this cycle instead of the next one
            await ScanAsync(cancellationToken).ConfigureAwait(false);
        }

        // the files were written before the scan, so nothing could be matched to a Jellyfin
        // item back then. The scan has just run, so this is where the match happens, and the
        // inspection right after has the identifiers it needs.
        ResolveScannedItems(server.Id);

        report.Probed = await _probe
            .ProbeAsync(_store.GetByFriendServer(server.Id), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("[ShadowLibrary] Synchronised {Name}. {Report}", server.Name, report);
        return report;
    }

    /// <summary>
    /// Resolves the folder of a friend server, freezing its folder name the first time so a
    /// later rename of the entry leaves the files where they are.
    /// </summary>
    private string ResolveServerFolder(FriendServer server)
    {
        if (string.IsNullOrEmpty(server.FolderName))
        {
            var taken = ConfigurationStore.Current.FriendServers
                .Where(s => s.Id != server.Id)
                .Select(MediaFileWriter.BuildFolderName);

            // an entry from an earlier version keeps the folder it already has on disk, which
            // is the one the display name produced back then
            server.FolderName = MediaFileWriter.ReserveFolderName(server.Name, server.Id, taken);
        }

        return MediaFileWriter.BuildServerFolder(ConfigurationStore.Current.MediaRootPath, server);
    }

    private async Task<bool> SyncMoviesAsync(
        FriendServer server,
        Catalogue catalogue,
        LocalCatalogue local,
        string moviesFolder,
        Dictionary<string, ImportedItem> known,
        HashSet<string> seen,
        SyncReport report,
        CancellationToken cancellationToken)
    {
        var changed = false;

        foreach (var movie in catalogue.Movies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!LocalCatalogue.HasExternalId(movie.ProviderIds))
            {
                report.Unidentified++;
                continue;
            }

            if (local.HasMovie(movie.ProviderIds))
            {
                report.AlreadyLocal++;
                continue;
            }

            if (!local.TryClaimMovie(movie.ProviderIds, server.Id, out var claimKeys))
            {
                // another friend server holds it, or this one lists it twice
                report.Duplicate++;
                continue;
            }

            seen.Add(movie.Id);

            var folder = Path.Combine(moviesFolder, MediaFileWriter.BuildTitleFolderName(movie.Name, movie.ProductionYear));
            known.TryGetValue(movie.Id, out var existing);

            changed |= await UpsertItemAsync(
                server,
                existing,
                movie.Id,
                ImportedItemKind.Movie,
                folder,
                MediaFileWriter.ComputeHash(movie, MediaFileWriter.BuildOriginTag(server)),
                claimKeys,
                movie.Name,
                (item, refresh) => _writer.WriteMovieAsync(
                    folder, movie, server, item.Id, catalogue.Session, refresh, cancellationToken),
                report,
                cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    private async Task<bool> SyncSeriesAsync(
        FriendServer server,
        Catalogue catalogue,
        LocalCatalogue local,
        string showsFolder,
        Dictionary<string, ImportedItem> known,
        HashSet<string> seen,
        SyncReport report,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var knownSeries = _store.GetSeriesByFriendServer(server.Id)
            .ToDictionary(s => s.RemoteSeriesId, StringComparer.Ordinal);
        var seenSeries = new HashSet<string>(StringComparer.Ordinal);

        var episodesBySeries = catalogue.Episodes
            .Where(e => e.IsPlaceable)
            .GroupBy(e => e.SeriesId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        foreach (var series in catalogue.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!LocalCatalogue.HasExternalId(series.ProviderIds))
            {
                // an unidentified series takes its episodes down with it
                report.Unidentified += episodesBySeries.TryGetValue(series.Id, out var unknown)
                    ? unknown.Length
                    : 0;
                continue;
            }

            if (!episodesBySeries.TryGetValue(series.Id, out var all) || all.Length == 0)
            {
                continue;
            }

            var episodes = new List<(RemoteEpisode Episode, string[] ClaimKeys)>();
            foreach (var candidate in all)
            {
                var season = candidate.ParentIndexNumber!.Value;
                var number = candidate.IndexNumber!.Value;

                if (local.HasEpisode(series.ProviderIds, season, number))
                {
                    report.AlreadyLocal++;
                    continue;
                }

                if (!local.TryClaimEpisode(series.ProviderIds, season, number, server.Id, out var episodeKeys))
                {
                    // another friend server holds it, or this one lists it twice
                    report.Duplicate++;
                    continue;
                }

                episodes.Add((candidate, episodeKeys));
            }

            if (episodes.Count == 0)
            {
                // a series with no importable episode gets no folder at all
                continue;
            }

            seenSeries.Add(series.Id);

            var seriesFolder = Path.Combine(
                showsFolder, MediaFileWriter.BuildTitleFolderName(series.Name, series.ProductionYear));
            var seriesHash = MediaFileWriter.ComputeHash(series, MediaFileWriter.BuildOriginTag(server));
            knownSeries.TryGetValue(series.Id, out var storedSeries);

            var seriesMoved = storedSeries is not null
                && !string.Equals(storedSeries.FolderPath, seriesFolder, StringComparison.Ordinal);

            if (storedSeries is null
                || seriesMoved
                || !string.Equals(storedSeries.MetadataHash, seriesHash, StringComparison.Ordinal)
                || !File.Exists(Path.Combine(seriesFolder, "tvshow.nfo")))
            {
                if (seriesMoved)
                {
                    _cleaner.RemoveSeries(storedSeries!);
                }

                await _writer.WriteSeriesAsync(
                    seriesFolder, series, server, catalogue.Session, storedSeries is not null, cancellationToken)
                    .ConfigureAwait(false);

                _store.UpsertSeries(new ImportedSeries
                {
                    FriendServerId = server.Id,
                    RemoteSeriesId = series.Id,
                    FolderPath = seriesFolder,
                    MetadataHash = seriesHash,
                    LastImportUtc = DateTime.UtcNow
                });

                changed = true;
            }

            foreach (var (episode, episodeKeys) in episodes)
            {
                seen.Add(episode.Id);
                known.TryGetValue(episode.Id, out var existing);

                var seasonFolder = Path.Combine(
                    seriesFolder, MediaFileWriter.BuildSeasonFolderName(episode.ParentIndexNumber!.Value));

                changed |= await UpsertItemAsync(
                    server,
                    existing,
                    episode.Id,
                    ImportedItemKind.Episode,
                    seasonFolder,
                    MediaFileWriter.ComputeHash(episode),
                    episodeKeys,
                    series.Name + " " + MediaFileWriter.BuildEpisodeBaseName(episode),
                    (item, refresh) => _writer.WriteEpisodeAsync(
                        seasonFolder, episode, item.Id, catalogue.Session, refresh, cancellationToken),
                    report,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var orphan in knownSeries.Values.Where(s => !seenSeries.Contains(s.RemoteSeriesId)))
        {
            _cleaner.RemoveSeries(orphan);
            changed = true;
        }

        return changed;
    }

    private async Task<bool> UpsertItemAsync(
        FriendServer server,
        ImportedItem? existing,
        string remoteId,
        ImportedItemKind kind,
        string expectedFolder,
        string hash,
        string[] claimKeys,
        string label,
        Func<ImportedItem, bool, Task<GeneratedFiles>> write,
        SyncReport report,
        CancellationToken cancellationToken)
    {
        if (existing is not null
            && string.Equals(existing.MetadataHash, hash, StringComparison.Ordinal)
            && string.Equals(existing.FolderPath, expectedFolder, StringComparison.Ordinal)
            && File.Exists(existing.StrmPath))
        {
            report.Unchanged++;

            await _writer.EnsureStreamUrlAsync(existing.StrmPath, existing.Id, cancellationToken)
                .ConfigureAwait(false);

            var touched = ClearUnavailability(existing);
            touched |= ResolveLocalItem(existing);
            touched |= UpdateClaimKeys(existing, claimKeys);
            if (touched)
            {
                _store.Upsert(existing);
            }

            return false;
        }

        try
        {
            var item = existing ?? new ImportedItem
            {
                FriendServerId = server.Id,
                RemoteItemId = remoteId,
                Kind = kind
            };

            var files = await write(item, existing is not null).ConfigureAwait(false);

            if (existing is not null
                && !string.Equals(existing.StrmPath, files.StrmPath, StringComparison.Ordinal))
            {
                // renamed on the friend side, the files under the old name are now stale
                _cleaner.RemoveFiles(existing);
            }

            item.Kind = kind;
            item.FolderPath = files.FolderPath;
            item.StrmPath = files.StrmPath;
            item.NfoPath = files.NfoPath;
            item.MetadataHash = hash;
            item.ClaimKeys = claimKeys;
            item.LastImportUtc = DateTime.UtcNow;
            item.UnavailableSinceUtc = null;
            ResolveLocalItem(item);

            _store.Upsert(item);

            if (existing is null)
            {
                report.Added++;
            }
            else
            {
                report.Updated++;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            report.Failed++;
            _logger.LogError(ex, "[ShadowLibrary] Could not import {Label} from {Server}.", label, server.Name);
            return false;
        }
    }

    private async Task<Catalogue?> ListCatalogueAsync(FriendServer server, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetAsync(server, false, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var attempt = await TryListAsync(server, session, cancellationToken).ConfigureAwait(false);
        if (attempt is not null)
        {
            return attempt;
        }

        // a stored token that the friend server no longer accepts would otherwise fail every
        // cycle and eventually trip the removal threshold, so retry once with a fresh session
        var refreshed = await _sessions.GetAsync(server, true, cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            return null;
        }

        return await TryListAsync(server, refreshed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Catalogue?> TryListAsync(
        FriendServer server,
        FriendServerSession session,
        CancellationToken cancellationToken)
    {
        var ping = await _client
            .TryPingPluginAsync(session.Url, session.AccessToken, session.DeviceId, cancellationToken)
            .ConfigureAwait(false);
        var mode = ping is null ? FriendServerMode.Standard : FriendServerMode.Federated;

        var libraryIds = server.SyncAllLibraries ? Array.Empty<string>() : server.LibraryIds;

        try
        {
            var movies = await _client.GetMoviesAsync(
                session.Url,
                session.AccessToken,
                session.RemoteUserId,
                session.DeviceId,
                libraryIds,
                cancellationToken).ConfigureAwait(false);

            var series = await _client.GetSeriesAsync(
                session.Url,
                session.AccessToken,
                session.RemoteUserId,
                session.DeviceId,
                libraryIds,
                cancellationToken).ConfigureAwait(false);

            var episodes = await _client.GetEpisodesAsync(
                session.Url,
                session.AccessToken,
                session.RemoteUserId,
                session.DeviceId,
                libraryIds,
                cancellationToken).ConfigureAwait(false);

            if (mode == FriendServerMode.Federated)
            {
                var native = await _client.GetNativeItemIdsAsync(
                    session.Url,
                    session.AccessToken,
                    session.DeviceId,
                    cancellationToken).ConfigureAwait(false);
                movies = movies.Where(m => native.Contains(m.Id)).ToArray();
                episodes = episodes.Where(e => native.Contains(e.Id)).ToArray();
            }

            return new Catalogue(movies, series, episodes, mode, session);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogInformation("[ShadowLibrary] {Name} rejected the stored session.", server.Name);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] Could not list the catalogue of {Name}.", server.Name);
            return null;
        }
    }

    private void ResolveScannedItems(Guid friendServerId)
    {
        foreach (var item in _store.GetByFriendServer(friendServerId).Where(i => i.LocalItemId is null))
        {
            if (ResolveLocalItem(item))
            {
                _store.Upsert(item);
            }
        }
    }

    /// <summary>
    /// Runs a library scan and waits for it to be over.
    /// </summary>
    /// <remarks>
    /// Neither of the two obvious calls does this. QueueLibraryScan queues, and in 10.11
    /// ValidateMediaLibrary queues too, it hands RefreshMediaLibraryTask to the task manager
    /// and returns a completed task. Awaiting it returns before a single folder has been
    /// walked, and the items this cycle just wrote do not exist yet, so nothing can be
    /// resolved or inspected. Running the task and waiting on its completion event is what
    /// gives the rest of the cycle something to work with.
    /// </remarks>
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var worker = _taskManager.ScheduledTasks.FirstOrDefault(
            t => string.Equals(t.ScheduledTask.Key, LibraryScanTaskKey, StringComparison.Ordinal));

        if (worker is null)
        {
            _logger.LogWarning(
                "[ShadowLibrary] The {Key} task is not registered on this server. Queuing a scan instead, "
                + "the items of this cycle will be inspected on the next one.",
                LibraryScanTaskKey);
            _libraryManager.QueueLibraryScan();
            return;
        }

        // asynchronous continuations, otherwise the wait below would resume inline on the
        // thread the task manager raises its event from
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnTaskCompleted(object? sender, TaskCompletionEventArgs args)
        {
            if (string.Equals(args.Task.ScheduledTask.Key, LibraryScanTaskKey, StringComparison.Ordinal))
            {
                completed.TrySetResult();
            }
        }

        // subscribed before the task is queued, so a scan that finishes quickly cannot slip by
        _taskManager.TaskCompleted += OnTaskCompleted;

        try
        {
            // queuing rather than executing, so a scan already under way is waited on instead
            // of throwing, and ours runs after it
            _taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ScanTimeout);

            await completed.Task.WaitAsync(budget.Token).ConfigureAwait(false);

            // a scan already under way when this cycle queued its own ends first, so the state
            // is what says the queue behind it is drained rather than the first event
            while (worker.State != TaskState.Idle)
            {
                await Task.Delay(ScanPollInterval, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[ShadowLibrary] The library scan is still running after {Hours} hours. The items of this "
                + "cycle will be inspected on the next one.",
                ScanTimeout.TotalHours);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ShadowLibrary] The library scan after the cycle failed.");
        }
        finally
        {
            _taskManager.TaskCompleted -= OnTaskCompleted;
        }
    }

    private int MarkUnavailable(FriendServer server, IEnumerable<ImportedItem> items, SyncReport report)
    {
        var threshold = TimeSpan.FromHours(Math.Max(1, ConfigurationStore.Current.UnavailabilityThresholdHours));
        var now = DateTime.UtcNow;
        var removed = 0;

        foreach (var item in items)
        {
            if (item.UnavailableSinceUtc is null)
            {
                item.UnavailableSinceUtc = now;
                _store.Upsert(item);
                report.Unavailable++;
                continue;
            }

            if (now - item.UnavailableSinceUtc.Value >= threshold)
            {
                _logger.LogInformation(
                    "[ShadowLibrary] Removing {Path}, unavailable from {Name} for more than {Hours} hours.",
                    item.StrmPath,
                    server.Name,
                    threshold.TotalHours);
                _cleaner.Remove(item);
                removed++;
            }
            else
            {
                report.Unavailable++;
            }
        }

        return removed;
    }

    private static bool UpdateClaimKeys(ImportedItem item, string[] claimKeys)
    {
        if (item.ClaimKeys.SequenceEqual(claimKeys, StringComparer.Ordinal))
        {
            return false;
        }

        item.ClaimKeys = claimKeys;
        return true;
    }

    private static bool ClearUnavailability(ImportedItem item)
    {
        if (item.UnavailableSinceUtc is null)
        {
            return false;
        }

        item.UnavailableSinceUtc = null;
        return true;
    }

    private bool ResolveLocalItem(ImportedItem item)
    {
        if (item.LocalItemId is not null || string.IsNullOrEmpty(item.StrmPath))
        {
            return false;
        }

        var found = _libraryManager.FindByPath(item.StrmPath, false);
        if (found is null)
        {
            return false;
        }

        item.LocalItemId = found.Id;
        return true;
    }

    private sealed record Catalogue(
        IReadOnlyList<RemoteMovie> Movies,
        IReadOnlyList<RemoteSeries> Series,
        IReadOnlyList<RemoteEpisode> Episodes,
        FriendServerMode Mode,
        FriendServerSession Session);
}
