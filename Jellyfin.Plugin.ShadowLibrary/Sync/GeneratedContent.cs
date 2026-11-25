using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Tells generated files from the user's, and only ever removes the former. The media root
/// can be pointed at a folder that already holds media.
/// </summary>
public static partial class GeneratedContent
{
    /// <summary>
    /// Name of the file that keeps a declared folder from being empty.
    /// </summary>
    public const string MarkerFileName = ".shadowlibrary";

    private const long MaxStrmLength = 8192;

    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".bif"
    };

    private static readonly EnumerationOptions Listing = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false
    };

    /// <summary>
    /// Tells whether files may be written to a title folder.
    /// </summary>
    /// <param name="folderPath">Folder of a movie or a series.</param>
    /// <returns>True when the folder is new, empty or generated.</returns>
    public static bool CanWriteTo(string folderPath)
        => !Directory.Exists(folderPath)
            || !Directory.EnumerateFileSystemEntries(folderPath, "*", Listing).Any()
            || IsGenerated(folderPath);

    /// <summary>
    /// Removes the generated files of a title folder, and the folder itself when nothing else
    /// is in it.
    /// </summary>
    /// <param name="folderPath">Folder of a movie or a series.</param>
    public static void RemoveTitle(string folderPath)
    {
        // a link leads to files that live somewhere else
        if (!Directory.Exists(folderPath) || IsLink(folderPath) || !IsGenerated(folderPath))
        {
            return;
        }

        if (HoldsOnlyGenerated(folderPath))
        {
            Directory.Delete(folderPath, true);
            return;
        }

        RemoveGeneratedFiles(folderPath);
    }

    /// <summary>
    /// Removes a generated .strm along with the files named after it.
    /// </summary>
    /// <param name="strmPath">Path of the .strm.</param>
    public static void RemoveStrm(string strmPath)
    {
        var folder = Path.GetDirectoryName(strmPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || !IsGeneratedStrm(strmPath))
        {
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(strmPath);
        var siblings = Directory.EnumerateFiles(folder, "*", Listing)
            .Where(f => IsNamedAfter(Path.GetFileName(f), baseName))
            .Where(f => !string.Equals(f, strmPath, StringComparison.Ordinal))
            .ToArray();

        // next to a media file of the same name, the subtitles and metadata are its own
        if (siblings.All(f => SidecarExtensions.Contains(Path.GetExtension(f))))
        {
            foreach (var sibling in siblings)
            {
                File.Delete(sibling);
            }

            var trickplay = Path.Combine(folder, baseName + ".trickplay");
            if (Directory.Exists(trickplay) && !IsLink(trickplay))
            {
                Directory.Delete(trickplay, true);
            }
        }

        File.Delete(strmPath);
    }

    /// <summary>
    /// Removes the generated titles of a Movies or Shows folder, then the folder itself when
    /// nothing is left.
    /// </summary>
    /// <param name="folderPath">Declared folder.</param>
    public static void RemoveDeclaredFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath) || IsLink(folderPath))
        {
            return;
        }

        foreach (var title in Directory.GetDirectories(folderPath, "*", Listing))
        {
            RemoveTitle(title);
        }

        File.Delete(Path.Combine(folderPath, MarkerFileName));
        RemoveIfEmpty(folderPath);
    }

    /// <summary>
    /// Tells whether a server folder holds nothing but generated titles.
    /// </summary>
    /// <param name="serverFolder">Folder of a friend server.</param>
    /// <returns>True when it can be moved as a whole.</returns>
    public static bool IsWholeServerGenerated(string serverFolder)
    {
        if (IsLink(serverFolder))
        {
            return false;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(serverFolder, "*", Listing))
        {
            var name = Path.GetFileName(entry);
            if (!Directory.Exists(entry)
                || IsLink(entry)
                || (name != MediaFileWriter.MoviesFolderName && name != MediaFileWriter.ShowsFolderName))
            {
                return false;
            }

            foreach (var title in Directory.EnumerateFileSystemEntries(entry, "*", Listing))
            {
                var generated = Directory.Exists(title)
                    ? !IsLink(title) && IsGenerated(title) && HoldsOnlyGenerated(title)
                    : Path.GetFileName(title) == MarkerFileName;
                if (!generated)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes a folder that holds nothing.
    /// </summary>
    /// <param name="folderPath">Folder.</param>
    public static void RemoveIfEmpty(string folderPath)
    {
        if (Directory.Exists(folderPath)
            && !IsLink(folderPath)
            && !Directory.EnumerateFileSystemEntries(folderPath, "*", Listing).Any())
        {
            Directory.Delete(folderPath);
        }
    }

    /// <summary>
    /// Tells whether a .strm points at the relay.
    /// </summary>
    /// <param name="path">File.</param>
    /// <returns>True for a generated .strm.</returns>
    public static bool IsGeneratedStrm(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var info = new FileInfo(path);
        return info.Exists
            && info.Length <= MaxStrmLength
            && File.ReadAllText(path).Contains(ImportedItems.RelayPathMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenerated(string folderPath)
        => Directory.EnumerateFiles(folderPath, "*.nfo", Listing).Any(IsTaggedNfo)
            || FilesUnder(folderPath).Any(IsGeneratedStrm);

    private static bool IsTaggedNfo(string path)
        => string.Equals(Path.GetExtension(path), ".nfo", StringComparison.OrdinalIgnoreCase)
            && File.ReadAllText(path).Contains("<tag>" + MediaFileWriter.OriginTagPrefix, StringComparison.Ordinal);

    private static bool HoldsOnlyGenerated(string folderPath)
    {
        var names = Directory.EnumerateFiles(folderPath, "*.strm", Listing)
            .Where(IsGeneratedStrm)
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToArray();

        foreach (var file in Directory.EnumerateFiles(folderPath, "*", Listing))
        {
            if (IsLink(file))
            {
                return false;
            }

            var name = Path.GetFileName(file);
            var known = IsGeneratedStrm(file)
                || IsTaggedNfo(file)
                || KnownSidecar().IsMatch(name)
                || (SidecarExtensions.Contains(Path.GetExtension(file)) && names.Any(n => IsNamedAfter(name, n)));
            if (!known)
            {
                return false;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(folderPath, "*", Listing))
        {
            var isTrickplay = names.Any(n => Path.GetFileName(directory) == n + ".trickplay");
            if (IsLink(directory) || (!isTrickplay && !HoldsOnlyGenerated(directory)))
            {
                return false;
            }
        }

        return true;
    }

    private static void RemoveGeneratedFiles(string folderPath)
    {
        foreach (var directory in Directory.GetDirectories(folderPath, "*", Listing).Where(d => !IsLink(d)))
        {
            RemoveGeneratedFiles(directory);
        }

        foreach (var strm in Directory.GetFiles(folderPath, "*.strm", Listing).Where(IsGeneratedStrm))
        {
            RemoveStrm(strm);
        }

        foreach (var nfo in Directory.GetFiles(folderPath, "*.nfo", Listing).Where(IsTaggedNfo))
        {
            File.Delete(nfo);
        }

        RemoveIfEmpty(folderPath);
    }

    private static IEnumerable<string> FilesUnder(string folderPath)
        => Directory.EnumerateFiles(folderPath, "*", Listing)
            .Concat(Directory.EnumerateDirectories(folderPath, "*", Listing).Where(d => !IsLink(d)).SelectMany(FilesUnder));

    private static bool IsNamedAfter(string fileName, string baseName)
        => fileName.StartsWith(baseName + ".", StringComparison.Ordinal)
            || fileName.StartsWith(baseName + "-", StringComparison.Ordinal);

    private static bool IsLink(string path)
        => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    // written by the plugin, or by Jellyfin when it stores artwork and metadata beside the media
    [GeneratedRegex(
        @"^(season[\w ]*-)?(poster|folder|logo|clearlogo|clearart|art|fanart|backdrop|landscape|thumb|banner|disc|discart|cover|default)[-\d]*\.(jpe?g|png|webp)$|^(movie|tvshow|season)\.nfo$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KnownSidecar();
}
