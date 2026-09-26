using System.Collections.Concurrent;

namespace Jellyfin.Plugin.ShadowLibrary.Api;

/// <summary>
/// Remembers which friend server source a relayed item plays from, so the burst of range
/// requests ffmpeg sends when a playback starts or seeks skips the playback info round trip.
/// </summary>
public class PlaybackTargetCache
{
    // long enough to cover a start and the seeks around it, short enough that a play
    // session the friend server dropped is not reused for long
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, (PlaybackTarget Target, DateTime ExpiresUtc)> _entries = new();

    /// <summary>
    /// Looks up the source of an item.
    /// </summary>
    /// <param name="itemId">Plugin side item identifier.</param>
    /// <param name="target">The cached source, when there is a live one.</param>
    /// <returns>True when a live entry was found.</returns>
    public bool TryGet(Guid itemId, out PlaybackTarget target)
    {
        if (_entries.TryGetValue(itemId, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            target = entry.Target;
            return true;
        }

        target = default;
        return false;
    }

    /// <summary>
    /// Records the source of an item.
    /// </summary>
    /// <param name="itemId">Plugin side item identifier.</param>
    /// <param name="target">Source to remember.</param>
    public void Set(Guid itemId, PlaybackTarget target)
    {
        var now = DateTime.UtcNow;
        _entries[itemId] = (target, now + Lifetime);

        // pruned on write, entries are tiny and writes rare
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresUtc <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Forgets the source of an item, after the friend server refused it.
    /// </summary>
    /// <param name="itemId">Plugin side item identifier.</param>
    public void Remove(Guid itemId) => _entries.TryRemove(itemId, out _);
}
