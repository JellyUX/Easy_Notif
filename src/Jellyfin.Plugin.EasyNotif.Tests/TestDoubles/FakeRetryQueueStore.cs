using Jellyfin.Plugin.EasyNotif.Storage;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>In-memory <see cref="IRetryQueueStore"/> that stores detached copies, like the real store.</summary>
public sealed class FakeRetryQueueStore : IRetryQueueStore
{
    /// <summary>Gets the held entries.</summary>
    public List<RetryEntry> Entries { get; } = [];

    /// <inheritdoc/>
    public bool HasDue(DateTime nowUtc) => Entries.Any(e => e.NextAttemptUtc <= nowUtc);

    /// <inheritdoc/>
    public IReadOnlyList<RetryEntry> Due(DateTime nowUtc)
        => [.. Entries.Where(e => e.NextAttemptUtc <= nowUtc).Select(e => e.Clone())];

    /// <inheritdoc/>
    public RetryEntry? Get(string keyPrefix) => Entries.FirstOrDefault(e => e.KeyPrefix == keyPrefix)?.Clone();

    /// <inheritdoc/>
    public void Upsert(RetryEntry entry)
    {
        Entries.RemoveAll(e => e.KeyPrefix == entry.KeyPrefix);
        Entries.Add(entry.Clone());
    }

    /// <inheritdoc/>
    public void Remove(string keyPrefix) => Entries.RemoveAll(e => e.KeyPrefix == keyPrefix);
}
