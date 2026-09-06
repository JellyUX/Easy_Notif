using Jellyfin.Plugin.EasyNotif.Media;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IAddedItemsStore"/> for tests: <see cref="RecordAdded"/> stamps the item
/// with the current fake clock, <see cref="AddedSince"/> filters it, and start/stop are no-ops.
/// </summary>
public sealed class FakeAddedItemsStore : IAddedItemsStore
{
    private readonly List<(DateTime Ts, Guid Id)> _records = [];
    private readonly Func<DateTime> _now;

    /// <summary>Initializes a new instance of the <see cref="FakeAddedItemsStore"/> class.</summary>
    /// <param name="now">The clock; defaults to <see cref="DateTime.UtcNow"/>.</param>
    public FakeAddedItemsStore(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    /// <summary>Gets the ids passed to <see cref="RecordAdded"/>, in order.</summary>
    public List<Guid> Recorded { get; } = [];

    /// <summary>Records an item as added at an explicit time (test setup helper).</summary>
    /// <param name="id">The item id.</param>
    /// <param name="ts">The add time.</param>
    public void Seed(Guid id, DateTime ts) => _records.Add((ts, id));

    /// <inheritdoc/>
    public void RecordAdded(Guid itemId)
    {
        Recorded.Add(itemId);
        _records.Add((_now(), itemId));
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<Guid> AddedSince(DateTime sinceUtc)
        => _records.Where(r => r.Ts >= sinceUtc).Select(r => r.Id).ToHashSet();

    /// <inheritdoc/>
    public void Start()
    {
    }

    /// <inheritdoc/>
    public Task StopAsync() => Task.CompletedTask;
}
