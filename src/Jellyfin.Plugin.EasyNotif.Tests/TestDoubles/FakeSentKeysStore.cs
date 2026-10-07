using Jellyfin.Plugin.EasyNotif.Storage;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>In-memory <see cref="ISentKeysStore"/> with no retention.</summary>
public sealed class FakeSentKeysStore : ISentKeysStore
{
    /// <summary>Gets the recorded keys.</summary>
    public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool Contains(string key) => Keys.Contains(key);

    /// <inheritdoc/>
    public void Record(string key) => Keys.Add(key);
}
