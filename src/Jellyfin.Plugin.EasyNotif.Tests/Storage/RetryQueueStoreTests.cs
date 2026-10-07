using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="RetryQueueStore"/>: an entry survives a reload, only due entries are returned,
/// returned entries are detached copies, an upsert replaces by key prefix and a removal deletes.
/// </summary>
public sealed class RetryQueueStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-retryqueue-tests-" + Guid.NewGuid());
    private readonly List<RetryQueueStore> _stores = [];

    private RetryQueueStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new RetryQueueStore(paths.Object, new FileSystem(), NullLogger<RetryQueueStore>.Instance);
        _stores.Add(store);
        return store;
    }

    private static RetryEntry Entry(string prefix, DateTime next) => new()
    {
        CampaignId = "newsletter",
        KeyPrefix = prefix,
        RunUtc = T0,
        SinceUtc = T0.AddDays(-7),
        UserIds = [Guid.NewGuid()],
        Attempts = 1,
        FirstFailureUtc = T0,
        NextAttemptUtc = next
    };

    [Fact]
    public void Upsert_ThenReload_OnlyDueEntriesAreReturned()
    {
        var store = Build();
        store.Upsert(Entry("newsletter:a", T0.AddMinutes(15)));
        store.Upsert(Entry("newsletter:b", T0.AddHours(1)));

        var reloaded = Build();

        Assert.False(reloaded.HasDue(T0));
        var due = Assert.Single(reloaded.Due(T0.AddMinutes(15)));
        Assert.Equal("newsletter:a", due.KeyPrefix);
        Assert.Equal(T0.AddDays(-7), due.SinceUtc);
    }

    [Fact]
    public void Due_ReturnsDetachedCopies_UpsertReplaces_RemoveDeletes()
    {
        var store = Build();
        store.Upsert(Entry("newsletter:a", T0));

        var copy = store.Due(T0).Single();
        copy.UserIds.Clear();
        Assert.Single(store.Due(T0).Single().UserIds);

        copy.Attempts = 2;
        store.Upsert(copy);
        Assert.Equal(2, store.Due(T0).Single().Attempts);

        store.Remove("newsletter:a");
        Assert.False(Build().HasDue(T0.AddDays(1)));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
