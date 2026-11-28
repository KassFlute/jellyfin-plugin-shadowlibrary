using Jellyfin.Plugin.ShadowLibrary.Configuration;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Checks that the generated files can be written.
/// </summary>
public static class MediaRoot
{
    /// <summary>
    /// Creates a folder if needed and tries to write to it.
    /// </summary>
    /// <param name="path">Folder.</param>
    /// <returns>What is wrong with it, null when it can be used.</returns>
    public static string? Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "No folder is set.";
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return path + " is not a full path.";
        }

        if (File.Exists(path))
        {
            return path + " is a file, not a folder.";
        }

        var existed = Directory.Exists(path);

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path + " does not exist and Jellyfin, which runs as the user \"" + Environment.UserName
                + "\", could not create it: " + ex.Message;
        }

        try
        {
            var probe = Path.Combine(path, ".shadowlibrary-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path + (existed ? " exists but" : " was created but") + " Jellyfin, which runs as the user \""
                + Environment.UserName + "\", cannot write to it: " + ex.Message;
        }

        return null;
    }

    /// <summary>
    /// Throws when the folders of a friend server cannot be written.
    /// </summary>
    /// <param name="serverFolder">Generated folder of the friend server.</param>
    public static void EnsureUsable(string serverFolder)
    {
        foreach (var name in new[] { MediaFileWriter.MoviesFolderName, MediaFileWriter.ShowsFolderName })
        {
            var problem = Check(Path.Combine(serverFolder, name));
            if (problem is not null)
            {
                throw new IOException(problem);
            }
        }
    }

    /// <summary>
    /// Checks the configured media root.
    /// </summary>
    /// <returns>What is wrong with it, null when it can be used.</returns>
    public static string? CheckConfigured() => Check(ConfigurationStore.Current.MediaRootPath);
}
