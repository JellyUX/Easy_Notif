namespace Jellyfin.Plugin.EasyNotif.Configuration;

/// <summary>
/// Reads and persists the plugin configuration. Wraps the static <c>Plugin.Instance</c> so that
/// code depending on the configuration (the settings endpoints, the startup service, the email
/// sender) stays unit-testable with a fake.
/// </summary>
public interface IConfigAccessor
{
    /// <summary>Gets the live plugin configuration.</summary>
    /// <returns>The configuration.</returns>
    PluginConfiguration Get();

    /// <summary>Persists the current configuration to disk.</summary>
    void Save();
}

/// <summary>
/// Default <see cref="IConfigAccessor"/>, delegating to <see cref="Plugin.Instance"/>.
/// </summary>
public sealed class PluginConfigAccessor : IConfigAccessor
{
    /// <inheritdoc/>
    public PluginConfiguration Get() =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("The plugin instance is not available.");

    /// <inheritdoc/>
    public void Save() =>
        (Plugin.Instance ?? throw new InvalidOperationException("The plugin instance is not available.")).SaveConfiguration();
}
