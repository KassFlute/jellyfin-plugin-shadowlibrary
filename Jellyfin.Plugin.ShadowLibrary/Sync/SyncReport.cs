using System.Globalization;

namespace Jellyfin.Plugin.ShadowLibrary.Sync;

/// <summary>
/// What one synchronisation cycle did for one friend server.
/// </summary>
public class SyncReport
{
    /// <summary>
    /// Gets or sets a value indicating whether the friend server could be listed.
    /// </summary>
    public bool Reached { get; set; }

    /// <summary>
    /// Gets or sets the number of newly imported items.
    /// </summary>
    public int Added { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose metadata was rewritten.
    /// </summary>
    public int Updated { get; set; }

    /// <summary>
    /// Gets or sets the number of items left untouched.
    /// </summary>
    public int Unchanged { get; set; }

    /// <summary>
    /// Gets or sets the number of items removed.
    /// </summary>
    public int Removed { get; set; }

    /// <summary>
    /// Gets or sets the number of items left out because the local server already holds them.
    /// </summary>
    public int AlreadyLocal { get; set; }

    /// <summary>
    /// Gets or sets the number of items left out because another friend server already
    /// provides them, or because this one lists them twice.
    /// </summary>
    public int Duplicate { get; set; }

    /// <summary>
    /// Gets or sets the number of items left out because the friend server could not identify them.
    /// </summary>
    public int Unidentified { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose media was inspected for tracks.
    /// </summary>
    public int Probed { get; set; }

    /// <summary>
    /// Gets or sets the number of items that failed to import.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets the number of items currently counted as unavailable.
    /// </summary>
    public int Unavailable { get; set; }

    /// <inheritdoc />
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "reached={0} added={1} updated={2} unchanged={3} removed={4} alreadyLocal={5} "
        + "duplicate={6} unidentified={7} unavailable={8} probed={9} failed={10}",
        Reached,
        Added,
        Updated,
        Unchanged,
        Removed,
        AlreadyLocal,
        Duplicate,
        Unidentified,
        Unavailable,
        Probed,
        Failed);
}
