using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Removes imported items, both their generated files and their row in the store.
/// </summary>
public class ImportedMediaCleaner
{
    private readonly ImportedItemStore _store;
    private readonly ILogger<ImportedMediaCleaner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportedMediaCleaner"/> class.
    /// </summary>
    /// <param name="store">Imported item store.</param>
    /// <param name="logger">Logger.</param>
    public ImportedMediaCleaner(ImportedItemStore store, ILogger<ImportedMediaCleaner> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Removes one item, files and row.
    /// </summary>
    /// <param name="item">Item to remove.</param>
    public void Remove(ImportedItem item)
    {
        RemoveFiles(item);
        _store.Delete(item.Id);
    }

    /// <summary>
    /// Removes the files of one item and leaves its row alone, for an item that is being
    /// rewritten somewhere else.
    /// </summary>
    /// <param name="item">Item whose files should go.</param>
    public void RemoveFiles(ImportedItem item)
    {
        if (item.Kind == ImportedItemKind.Movie)
        {
            // a movie owns its folder
            DeleteFolder(item.FolderPath);
            return;
        }

        // an episode shares its season folder with its siblings, so only its own files go.
        // Anything named after it counts, subtitles Jellyfin fetched afterwards included,
        // otherwise they would linger and keep the season folder from being pruned.
        foreach (var file in SiblingsOf(item.StrmPath))
        {
            DeleteFile(file);
        }

        PruneIfEmpty(item.FolderPath);
    }

    /// <summary>
    /// Removes a series folder and everything under it.
    /// </summary>
    /// <param name="series">Series to remove.</param>
    public void RemoveSeries(ImportedSeries series)
    {
        DeleteFolder(series.FolderPath);
        _store.DeleteSeries(series.FriendServerId, series.RemoteSeriesId);
    }

    /// <summary>
    /// Removes every item imported from a friend server, then prunes the folders left empty.
    /// </summary>
    /// <param name="friendServerId">Friend server identifier.</param>
    /// <returns>How many items were removed.</returns>
    public int RemoveAllForServer(Guid friendServerId)
    {
        var items = _store.GetByFriendServer(friendServerId);
        var series = _store.GetSeriesByFriendServer(friendServerId);
        var ancestors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var one in series)
        {
            Collect(ancestors, one.FolderPath);
            RemoveSeries(one);
        }

        foreach (var item in items)
        {
            // the display name may have changed since the import, so the folders to prune
            // come from the stored paths rather than from the current configuration
            Collect(ancestors, item.FolderPath);
            Remove(item);
        }

        // deepest first, so a folder is only considered once its children are gone
        foreach (var folder in ancestors.OrderByDescending(f => f.Length))
        {
            PruneIfEmpty(folder);
        }

        return items.Count;
    }

    /// <summary>
    /// Deletes the whole folder generated for a friend server, for an entry that is being
    /// removed. Nothing outside the media root is ever touched.
    /// </summary>
    /// <param name="server">Friend server being removed.</param>
    public void RemoveServerFolder(FriendServer server)
    {
        // an entry that never completed a cycle has no recorded folder, so fall back to the
        // one the current settings point at
        var folder = string.IsNullOrEmpty(server.GeneratedFolderPath)
            ? MediaFileWriter.BuildServerFolder(ConfigurationStore.Current.MediaRootPath, server)
            : server.GeneratedFolderPath;

        if (IsInsideMediaRoot(folder))
        {
            DeleteFolder(folder);
        }
    }

    private static void Collect(HashSet<string> ancestors, string path)
    {
        var current = Path.GetDirectoryName(path);
        for (var depth = 0; depth < 3 && !string.IsNullOrEmpty(current); depth++)
        {
            if (!IsInsideMediaRoot(current))
            {
                return;
            }

            ancestors.Add(current);
            current = Path.GetDirectoryName(current);
        }
    }

    /// <summary>
    /// Tells whether a folder is one the plugin generated, so pruning stops at the media root
    /// instead of walking into folders that belong to the user.
    /// </summary>
    private static bool IsInsideMediaRoot(string folderPath)
    {
        var root = Path.TrimEndingDirectorySeparator(ConfigurationStore.Current.MediaRootPath ?? string.Empty);
        if (root.Length == 0)
        {
            return false;
        }

        var candidate = Path.TrimEndingDirectorySeparator(folderPath);
        return candidate.Length > root.Length
            && candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && candidate[root.Length] == Path.DirectorySeparatorChar;
    }

    private string[] SiblingsOf(string strmPath)
    {
        var folder = Path.GetDirectoryName(strmPath);
        var baseName = Path.GetFileNameWithoutExtension(strmPath);

        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(baseName) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(file =>
                {
                    var name = Path.GetFileName(file);
                    return name.StartsWith(baseName + ".", StringComparison.Ordinal)
                        || name.StartsWith(baseName + "-", StringComparison.Ordinal);
                })
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[ShadowLibrary] Could not list the files of {Path}.", strmPath);
            return [];
        }
    }

    private void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[ShadowLibrary] Could not delete {File}.", path);
        }
    }

    private void DeleteFolder(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            Directory.Delete(folderPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[ShadowLibrary] Could not delete {Folder}.", folderPath);
        }
    }

    private void PruneIfEmpty(string folderPath)
    {
        if (!IsInsideMediaRoot(folderPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(folderPath) && !Directory.EnumerateFileSystemEntries(folderPath).Any())
            {
                Directory.Delete(folderPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[ShadowLibrary] Left {Folder} in place.", folderPath);
        }
    }
}
