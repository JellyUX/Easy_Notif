using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Media;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="AddedItemsStore"/>: queued ids are drained and persisted with the server
/// timestamp, <see cref="AddedItemsStore.AddedSince"/> windows them, entries persist across
/// instances, and old entries are compacted out.
/// </summary>
public sealed class AddedItemsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-added-tests-" + Guid.NewGuid());
    private readonly List<AddedItemsStore> _stores = [];
    private DateTime _now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private AddedItemsStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        // No real delay: the drain loop's debounce completes immediately.
        var store = new AddedItemsStore(
            paths.Object, new FileSystem(), NullLogger<AddedItemsStore>.Instance, () => _now, (_, _) => Task.CompletedTask);
        _stores.Add(store);
        return store;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met in time");
    }

    [Fact]
    public async Task RecordAdded_IsDrainedAndPersistedWithTheServerTime()
    {
        var store = Build();
        store.Start();
        var id = Guid.NewGuid();

        store.RecordAdded(id);
        await WaitFor(() => store.AddedSince(_now.AddMinutes(-1)).Contains(id));

        Assert.Contains(id, store.AddedSince(_now.AddMinutes(-1)));
        Assert.Empty(store.AddedSince(_now.AddMinutes(1)));
        await store.StopAsync();
    }

    [Fact]
    public async Task Entries_PersistAcrossInstances()
    {
        var first = Build();
        first.Start();
        var id = Guid.NewGuid();
        first.RecordAdded(id);
        await WaitFor(() => first.AddedSince(_now.AddMinutes(-1)).Contains(id));
        await first.StopAsync();

        Assert.Contains(id, Build().AddedSince(_now.AddMinutes(-1)));
    }

    [Fact]
    public async Task OldEntries_AreCompactedOnTheNextWrite()
    {
        var store = Build();
        store.Start();

        var oldId = Guid.NewGuid();
        store.RecordAdded(oldId);
        await WaitFor(() => store.AddedSince(_now.AddDays(-1)).Contains(oldId));

        _now = _now.AddDays(120); // past the 90-day retention
        var freshId = Guid.NewGuid();
        store.RecordAdded(freshId);
        await WaitFor(() => store.AddedSince(_now.AddMinutes(-1)).Contains(freshId));

        Assert.DoesNotContain(oldId, store.AddedSince(DateTime.MinValue));
        await store.StopAsync();
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
