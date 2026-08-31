using Jellyfin.Plugin.ShadowLibrary.Configuration;
using Jellyfin.Plugin.ShadowLibrary.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Moves the generated files of a friend server when the folder they belong in changes,
/// which happens when the media root is changed in the settings.
/// </summary>
/// <remarks>
/// The folder name of a friend server is frozen when the entry is created, so renaming an
/// entry never lands here. What does is a new media root, and there the whole tree has to
/// follow, otherwise the library keeps pointing at the old location and the next cycle
/// rewrites everything somewhere no library looks at.
/// </remarks>
public class GeneratedPathMigrator
{
    private readonly ImportedItemStore _store;
    private readonly ImportedMediaCleaner _cleaner;
    private readonly LibraryAttacher _attacher;
    private readonly ILogger<GeneratedPathMigrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeneratedPathMigrator"/> class.
    /// </summary>
    /// <param name="store">Imported item store.</param>
    /// <param name="cleaner">Imported media cleaner.</param>
    /// <param name="attacher">Library attacher.</param>
    /// <param name="logger">Logger.</param>
    public GeneratedPathMigrator(
        ImportedItemStore store,
        ImportedMediaCleaner cleaner,
        LibraryAttacher attacher,
        ILogger<GeneratedPathMigrator> logger)
    {
        _store = store;
        _cleaner = cleaner;
        _attacher = attacher;
        _logger = logger;
    }

    /// <summary>
    /// Brings the files of a friend server to the folder the current settings ask for.
    /// </summary>
    /// <param name="server">Friend server, updated in place.</param>
    /// <param name="expectedFolder">Folder the current settings ask for.</param>
    /// <returns>True when something moved, so the library has to be scanned again.</returns>
    public bool Migrate(FriendServer server, string expectedFolder)
    {
        var current = server.GeneratedFolderPath;

        if (string.IsNullOrEmpty(current))
        {
            // first cycle, or an entry from a version that did not record it
            server.GeneratedFolderPath = expectedFolder;
            return false;
        }

        if (string.Equals(current, expectedFolder, StringComparison.Ordinal))
        {
            return false;
        }

        _logger.LogInformation(
            "[ShadowLibrary] The generated folder of {Name} moves from {Old} to {New}.",
            server.Name,
            current,
            expectedFolder);

        // the libraries must stop pointing at the old location before anything moves, and
        // forgetting the attachment is what makes the next Attach declare the new one
        _attacher.Detach(server);
        server.AttachedMovieLibraryName = string.Empty;
        server.AttachedMoviePath = string.Empty;
        server.AttachedShowLibraryName = string.Empty;
        server.AttachedShowPath = string.Empty;
        server.GeneratedFolderPath = expectedFolder;

        if (!Directory.Exists(current))
        {
            _store.Repath(server.Id, current, expectedFolder);
            return true;
        }

        try
        {
            var parent = Path.GetDirectoryName(expectedFolder);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            Directory.Move(current, expectedFolder);
            _store.Repath(server.Id, current, expectedFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a move across filesystems, or a destination already taken. Dropping everything
            // costs one import cycle, leaving it half moved would cost a lot more
            _logger.LogError(
                ex,
                "[ShadowLibrary] Could not move {Old} to {New}. Everything imported from {Name} is dropped and will be imported again.",
                current,
                expectedFolder,
                server.Name);
            _cleaner.RemoveAllForServer(server.Id);
        }

        return true;
    }
}
