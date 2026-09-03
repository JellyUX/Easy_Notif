using Jellyfin.Plugin.EasyNotif.Configuration;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IConfigAccessor"/> for tests: holds one <see cref="PluginConfiguration"/>
/// and counts <see cref="Save"/> calls. <c>Plugin.Instance</c> is null in a unit-test run, so the
/// real accessor cannot be used.
/// </summary>
public sealed class FakeConfigAccessor : IConfigAccessor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FakeConfigAccessor"/> class.
    /// </summary>
    /// <param name="config">The starting configuration, or a fresh one.</param>
    public FakeConfigAccessor(PluginConfiguration? config = null) => Config = config ?? new PluginConfiguration();

    /// <summary>Gets the held configuration.</summary>
    public PluginConfiguration Config { get; }

    /// <summary>Gets the number of times <see cref="Save"/> has been called.</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc/>
    public PluginConfiguration Get() => Config;

    /// <inheritdoc/>
    public void Save() => SaveCount++;
}
