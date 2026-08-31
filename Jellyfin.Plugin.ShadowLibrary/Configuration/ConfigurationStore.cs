namespace Jellyfin.Plugin.ShadowLibrary.Configuration;

/// <summary>
/// Single entry point for reading and writing the plugin configuration. Everything that
/// mutates it goes through here, so concurrent writers cannot lose each other's changes.
/// </summary>
public static class ConfigurationStore
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Gets the live configuration.
    /// </summary>
    public static PluginConfiguration Current =>
        Plugin.Instance?.Configuration
        ?? throw new InvalidOperationException("The plugin is not initialised.");

    /// <summary>
    /// Mutates the configuration and persists it.
    /// </summary>
    /// <param name="mutate">Mutation to apply.</param>
    public static void Update(Action<PluginConfiguration> mutate)
    {
        lock (Gate)
        {
            var configuration = Current;
            mutate(configuration);
            Plugin.Instance!.UpdateConfiguration(configuration);
        }
    }

    /// <summary>
    /// Mutates the configuration, persists it and returns a value read under the same lock.
    /// </summary>
    /// <typeparam name="T">Returned type.</typeparam>
    /// <param name="mutate">Mutation to apply.</param>
    /// <returns>What the mutation returned.</returns>
    public static T Update<T>(Func<PluginConfiguration, T> mutate)
    {
        lock (Gate)
        {
            var configuration = Current;
            var result = mutate(configuration);
            Plugin.Instance!.UpdateConfiguration(configuration);
            return result;
        }
    }
}
