using Jellyfin.Plugin.ShadowLibrary.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Attaches the folders generated for a friend server to the libraries the user already has,
/// so imported media shows up beside their own instead of in a library of its own.
/// </summary>
public class LibraryAttacher
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryAttacher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryAttacher"/> class.
    /// </summary>
    /// <param name="libraryManager">Local library manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryAttacher(ILibraryManager libraryManager, ILogger<LibraryAttacher> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Lists the libraries a friend server folder can be attached to.
    /// </summary>
    /// <returns>The movie and show libraries of this server.</returns>
    public IReadOnlyList<LocalLibrary> GetAttachableLibraries()
        => _libraryManager.GetVirtualFolders()
            .Where(folder => folder.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows)
            .Select(folder => new LocalLibrary
            {
                Name = folder.Name,
                CollectionType = folder.CollectionType == CollectionTypeOptions.movies ? "movies" : "tvshows"
            })
            .ToArray();

    /// <summary>
    /// Makes sure the generated folders of a friend server are declared in the chosen libraries.
    /// </summary>
    /// <param name="server">Friend server.</param>
    /// <param name="moviesFolder">Generated movies folder.</param>
    /// <param name="showsFolder">Generated shows folder.</param>
    /// <returns>True when something was attached.</returns>
    public bool Attach(FriendServer server, string moviesFolder, string showsFolder)
    {
        var movies = Reconcile(
            server.MovieLibraryName, moviesFolder, server.AttachedMovieLibraryName, server.AttachedMoviePath);
        server.AttachedMovieLibraryName = movies.Library;
        server.AttachedMoviePath = movies.Path;

        var shows = Reconcile(
            server.ShowLibraryName, showsFolder, server.AttachedShowLibraryName, server.AttachedShowPath);
        server.AttachedShowLibraryName = shows.Library;
        server.AttachedShowPath = shows.Path;

        return movies.Changed || shows.Changed;
    }

    /// <summary>
    /// Brings one side in line with what the configuration asks for: nothing when the wanted
    /// library and path already match, a detach of the old declaration otherwise, followed by
    /// an attach of the new one.
    /// </summary>
    private (bool Changed, string Library, string Path) Reconcile(
        string wantedLibrary,
        string wantedPath,
        string attachedLibrary,
        string attachedPath)
    {
        var wanted = string.IsNullOrWhiteSpace(wantedLibrary) ? string.Empty : wantedLibrary;
        if (string.Equals(wanted, attachedLibrary, StringComparison.Ordinal)
            && string.Equals(wantedPath, attachedPath, StringComparison.Ordinal))
        {
            return (false, attachedLibrary, attachedPath);
        }

        var changed = Detach(attachedLibrary, attachedPath);

        if (wanted.Length == 0 || !Attach(wanted, wantedPath))
        {
            return (changed, string.Empty, string.Empty);
        }

        return (true, wanted, wantedPath);
    }

    /// <summary>
    /// Takes the generated folders of a friend server back out of the libraries they were
    /// added to, leaving the libraries as they were before.
    /// </summary>
    /// <param name="server">Friend server being removed.</param>
    /// <returns>True when something was detached.</returns>
    public bool Detach(FriendServer server)
    {
        // an entry that has not run a cycle since the paths were recorded still has folders
        // declared under the name the settings produce, so fall back to those
        var folder = string.IsNullOrEmpty(server.GeneratedFolderPath)
            ? MediaFileWriter.BuildServerFolder(ConfigurationStore.Current.MediaRootPath, server)
            : server.GeneratedFolderPath;

        var moviePath = string.IsNullOrEmpty(server.AttachedMoviePath)
            ? Path.Combine(folder, MediaFileWriter.MoviesFolderName)
            : server.AttachedMoviePath;

        var showPath = string.IsNullOrEmpty(server.AttachedShowPath)
            ? Path.Combine(folder, MediaFileWriter.ShowsFolderName)
            : server.AttachedShowPath;

        var detached = Detach(server.AttachedMovieLibraryName, moviePath);
        detached |= Detach(server.AttachedShowLibraryName, showPath);
        return detached;
    }

    private bool Detach(string libraryName, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(libraryName) || string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        var library = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, libraryName, StringComparison.Ordinal));

        if (library is null || !library.Locations.Contains(folderPath, StringComparer.Ordinal))
        {
            return false;
        }

        try
        {
            _libraryManager.RemoveMediaPath(libraryName, folderPath);
            _logger.LogInformation("[ShadowLibrary] Removed {Path} from the library {Name}.", folderPath, libraryName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "[ShadowLibrary] Could not remove {Path} from the library {Name}.", folderPath, libraryName);
            return false;
        }
    }

    private bool Attach(string wanted, string folderPath)
    {
        var library = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.Ordinal));

        if (library is null)
        {
            _logger.LogWarning("[ShadowLibrary] Library {Name} does not exist, leaving {Path} unattached.", wanted, folderPath);
            return false;
        }

        if (library.Locations.Contains(folderPath, StringComparer.Ordinal))
        {
            // already declared, only the bookkeeping was missing
            return true;
        }

        try
        {
            Directory.CreateDirectory(folderPath);
            _libraryManager.AddMediaPath(wanted, new MediaPathInfo(folderPath));
            _logger.LogInformation("[ShadowLibrary] Added {Path} to the library {Name}.", folderPath, wanted);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "[ShadowLibrary] Could not add {Path} to the library {Name}.", folderPath, wanted);
            return false;
        }
    }
}

/// <summary>
/// A library of this server a friend folder can be attached to.
/// </summary>
public class LocalLibrary
{
    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection type, movies or tvshows.
    /// </summary>
    public string CollectionType { get; set; } = string.Empty;
}
