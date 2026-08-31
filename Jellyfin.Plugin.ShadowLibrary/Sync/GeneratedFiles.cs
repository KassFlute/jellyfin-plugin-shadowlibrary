namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// Where the files of one imported item ended up.
/// </summary>
/// <param name="FolderPath">Folder holding them.</param>
/// <param name="StrmPath">Generated .strm file.</param>
/// <param name="NfoPath">Generated .nfo file.</param>
public record GeneratedFiles(string FolderPath, string StrmPath, string NfoPath);
