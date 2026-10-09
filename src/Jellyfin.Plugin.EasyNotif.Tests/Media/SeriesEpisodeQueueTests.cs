using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="SeriesEpisodeQueue"/>: only real episodes of followed series are queued, a
/// removal drops the pending item and records its key, a removed followed series is purged, events
/// apply in arrival order, and nothing is written without a subscription.
/// </summary>
public sealed class SeriesEpisodeQueueTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Followed = Guid.NewGuid();

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-seriesqueue-tests-" + Guid.NewGuid());
    private readonly SeriesNotifyStore _store;
    private readonly SeriesEpisodeQueue _queue;
    private readonly FakeEasyNotifLog _log = new();

    public SeriesEpisodeQueueTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        _store = new SeriesNotifyStore(paths.Object, new FileSystem(), NullLogger<SeriesNotifyStore>.Instance);
        _queue = new SeriesEpisodeQueue(_store, _log, () => T0, (_, _) => Task.CompletedTask);
    }

    private static Episode Episode(Guid seriesId, int season = 1, int number = 1)
        => new() { Id = Guid.NewGuid(), SeriesId = seriesId, ParentIndexNumber = season, IndexNumber = number };

    [Fact]
    public async Task OnAdded_QueuesRealEpisodesOfFollowedSeriesOnly()
    {
        _store.Subscribe(Guid.NewGuid(), Followed, "bell", T0);
        var kept = Episode(Followed);
        var virtualEpisode = Episode(Followed, number: 2);
        virtualEpisode.IsVirtualItem = true;

        _queue.Start();
        _queue.OnAdded(kept);
        _queue.OnAdded(virtualEpisode);
        _queue.OnAdded(Episode(Guid.NewGuid()));
        _queue.OnAdded(new Movie { Id = Guid.NewGuid() });
        await _queue.StopAsync();

        var pending = Assert.Single(_store.Pending());
        Assert.Equal(kept.Id, pending.ItemId);
        Assert.Equal(Followed, pending.SeriesId);
        Assert.Equal(T0, pending.FirstSeen);
    }

    [Fact]
    public async Task Drain_RecordsTheFlush_AndALostBatch_InItsHealth()
    {
        _store.Subscribe(Guid.NewGuid(), Followed, "bell", T0);
        _queue.Start();
        _queue.OnAdded(Episode(Followed));
        await _queue.StopAsync();
        Assert.Equal(T0, _queue.Health.Snapshot().LastFlushUtc);

        var failing = new Mock<ISeriesNotifyStore>();
        failing.Setup(s => s.IsFollowed(It.IsAny<Guid>())).Returns(true);
        failing.Setup(s => s.Enqueue(It.IsAny<IEnumerable<(Guid, Guid)>>(), It.IsAny<DateTime>())).Throws(new IOException("disk full"));
        var log = new FakeEasyNotifLog();
        using var queue = new SeriesEpisodeQueue(failing.Object, log, () => T0, (_, _) => Task.CompletedTask);
        // Queued before the drain starts, so both events land in one batch.
        queue.OnAdded(Episode(Followed));
        queue.OnAdded(Episode(Followed, number: 2));
        queue.Start();
        await queue.StopAsync();

        var health = queue.Health.Snapshot();
        Assert.Equal(1, health.LostBatches);
        Assert.Equal(2, health.LostItems);
        Assert.Null(health.LastFlushUtc);
        var lost = Assert.Single(log.Entries, e => e.EventType == "drain.lost");
        Assert.Equal("episodes", lost.Fields!["drain"]);
    }

    [Fact]
    public async Task AddedThenRemoved_LeavesNothingPending_AndRecordsTheKey()
    {
        _store.Subscribe(Guid.NewGuid(), Followed, "bell", T0);
        var episode = Episode(Followed, season: 2, number: 5);

        _queue.Start();
        _queue.OnAdded(episode);
        _queue.OnRemoved(episode);
        await _queue.StopAsync();

        Assert.False(_store.HasPending);
        Assert.Contains($"ep:{Followed:N}:2:5", _store.RecentlyRemoved(T0));
    }

    [Fact]
    public async Task RemovedFollowedSeries_IsPurged_UnfollowedSeriesIsIgnored()
    {
        _store.Subscribe(Guid.NewGuid(), Followed, "bell", T0);
        _queue.Start();
        _queue.OnAdded(Episode(Followed));
        _queue.OnRemoved(new Series { Id = Guid.NewGuid() });
        _queue.OnRemoved(new Series { Id = Followed });
        await _queue.StopAsync();

        Assert.False(_store.HasSubscriptions);
        Assert.False(_store.HasPending);
        Assert.Empty(_store.RecentlyRemoved(T0));
    }

    [Fact]
    public async Task WithoutSubscription_NothingIsWritten()
    {
        var episode = Episode(Followed);

        _queue.Start();
        _queue.OnAdded(episode);
        _queue.OnRemoved(episode);
        await _queue.StopAsync();

        Assert.False(_store.HasPending);
        Assert.False(File.Exists(Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "series-notify.json")));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _queue.Dispose();
        _store.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
