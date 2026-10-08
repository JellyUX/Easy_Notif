using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="SeriesNotifyStore"/>: inert without a subscription (no file), episodes of
/// followed series only, state surviving a reload, detached copies, an entry leaving the queue once
/// every follower was served, removed keys, retention and the per-user daily send count.
/// </summary>
public sealed class SeriesNotifyStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid Series = Guid.NewGuid();

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-seriesnotify-tests-" + Guid.NewGuid());
    private readonly List<SeriesNotifyStore> _stores = [];

    private string FilePath => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "series-notify.json");

    private SeriesNotifyStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new SeriesNotifyStore(paths.Object, new FileSystem(), NullLogger<SeriesNotifyStore>.Instance);
        _stores.Add(store);
        return store;
    }

    [Fact]
    public void WithoutSubscription_NothingIsQueuedAndNoFileIsWritten()
    {
        var store = Build();

        Assert.Equal(0, store.Enqueue([(Guid.NewGuid(), Series)], T0));
        Assert.Equal(0, store.Prune(T0));

        Assert.False(store.HasSubscriptions);
        Assert.False(store.HasPending);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Enqueue_KeepsFollowedSeriesOnly_IgnoresKnownItems_SurvivesReload()
    {
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        var episode = Guid.NewGuid();

        Assert.Equal(1, store.Enqueue([(episode, Series), (Guid.NewGuid(), Guid.NewGuid())], T0));
        Assert.Equal(0, store.Enqueue([(episode, Series)], T0.AddMinutes(5)));

        var reloaded = Build();
        Assert.True(reloaded.IsFollowed(Series));
        Assert.True(reloaded.HasPending);
        var pending = Assert.Single(reloaded.Pending());
        Assert.Equal(episode, pending.ItemId);
        Assert.Equal(T0, pending.FirstSeen);
    }

    [Fact]
    public void Pending_ReturnsDetachedCopies()
    {
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        store.Enqueue([(Guid.NewGuid(), Series)], T0);

        store.Pending().Single().Notified.Add(UserA);

        Assert.Empty(store.Pending().Single().Notified);
    }

    [Fact]
    public void MarkNotified_RemovesTheEntryOnlyOnceEveryFollowerWasServed()
    {
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        store.Subscribe(UserB, Series, "bell", T0);
        var episode = Guid.NewGuid();
        store.Enqueue([(episode, Series)], T0);

        store.MarkNotified(UserA, [episode]);
        Assert.Equal([UserA], store.Pending().Single().Notified);

        store.MarkNotified(UserB, [episode]);
        Assert.False(store.HasPending);
    }

    [Fact]
    public void Unsubscribe_ThenPrune_DropsTheEntriesOfAnUnfollowedSeries()
    {
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        store.Enqueue([(Guid.NewGuid(), Series)], T0);

        store.Unsubscribe(UserA, Series);

        Assert.False(store.IsFollowed(Series));
        Assert.Equal(1, store.Prune(T0));
        Assert.False(store.HasPending);
    }

    [Fact]
    public void RecordRemoved_DropsThePendingItemAndRemembersTheKeyFor24Hours()
    {
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        var episode = Guid.NewGuid();
        store.Enqueue([(episode, Series)], T0);

        store.RecordRemoved(episode, "ep:x:1:2", T0);

        Assert.False(store.HasPending);
        Assert.Contains("ep:x:1:2", Build().RecentlyRemoved(T0.AddHours(23)));
        Assert.Empty(store.RecentlyRemoved(T0.AddHours(24)));
    }

    [Fact]
    public void Prune_DropsEntriesOlderThanSevenDays_DeferCountsAttempts()
    {
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();
        store.Enqueue([(old, Series)], T0);
        store.Enqueue([(recent, Series)], T0.AddDays(2));

        store.Defer([recent]);

        Assert.Equal(1, store.Prune(T0.AddDays(7)));
        var left = Assert.Single(store.Pending());
        Assert.Equal(recent, left.ItemId);
        Assert.Equal(1, left.Attempts);
    }

    /// <inheritdoc/>
    [Fact]
    public void RecordSend_CountsPerUserSinceATime_SurvivesReload_ForgetsAfterADay()
    {
        var store = Build();
        store.RecordSend(UserA, T0);
        store.RecordSend(UserA, T0.AddHours(2));
        store.RecordSend(UserB, T0.AddHours(2));

        var reloaded = Build();
        Assert.Equal(2, reloaded.SentSince(UserA, T0.Date));
        Assert.Equal(1, reloaded.SentSince(UserA, T0.AddHours(1)));
        Assert.Equal(1, reloaded.SentSince(UserB, T0.Date));

        reloaded.RecordSend(UserB, T0.AddHours(27));
        Assert.Equal(0, reloaded.SentSince(UserA, DateTime.MinValue));
    }

    [Fact]
    public void PurgeUser_ErasesEveryTraceOfTheUser_AndKeepsOthers()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $$"""
            { "Schema": 1, "Exclusions": [ { "UserId": "{{UserA}}", "SeriesId": "{{Series}}" }, { "UserId": "{{UserB}}", "SeriesId": "{{Series}}" } ] }
            """);
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        store.Subscribe(UserB, Series, "bell", T0);
        var item = Guid.NewGuid();
        store.Enqueue([(item, Series)], T0);
        store.MarkNotified(UserA, [item]);
        store.RecordSend(UserA, T0);

        store.PurgeUser(UserA);

        Assert.DoesNotContain(UserA.ToString(), File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal([UserB], store.Subscriptions().Select(s => s.UserId));
        Assert.Contains(UserB.ToString(), File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.SentSince(UserA, DateTime.MinValue));
        Assert.Empty(Assert.Single(store.Pending()).Notified);
    }

    [Fact]
    public void PurgeSeries_DropsItsSubscriptionsAndPendingEpisodes_Only()
    {
        var other = Guid.NewGuid();
        var store = Build();
        store.Subscribe(UserA, Series, "bell", T0);
        store.Subscribe(UserA, other, "bell", T0);
        store.Enqueue([(Guid.NewGuid(), Series), (Guid.NewGuid(), other)], T0);

        store.PurgeSeries(Series);

        Assert.False(store.IsFollowed(Series));
        Assert.True(store.IsFollowed(other));
        Assert.Equal(other, Assert.Single(store.Pending()).SeriesId);
        Assert.DoesNotContain(Series.ToString(), File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);
    }

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
