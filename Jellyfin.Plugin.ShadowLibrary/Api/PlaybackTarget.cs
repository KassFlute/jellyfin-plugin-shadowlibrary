namespace Jellyfin.Plugin.ShadowLibrary.Api;

/// <summary>
/// The friend server source a relay reads from.
/// </summary>
/// <param name="MediaSourceId">Media source identifier on the friend server.</param>
/// <param name="PlaySessionId">Play session the friend server opened for it.</param>
public readonly record struct PlaybackTarget(string? MediaSourceId, string? PlaySessionId);
