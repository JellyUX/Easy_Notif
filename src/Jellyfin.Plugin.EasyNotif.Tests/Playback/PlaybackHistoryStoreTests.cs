using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Playback;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Playback;

/// <summary>
/// Covers <see cref="PlaybackHistoryStore"/>: events are drained and persisted, the rollup is
/// incremented at write time and survives compaction, <see cref="PlaybackHistoryStore.GetWeek"/>
/// windows and deduplicates by item, compaction is a monthly no-op, and a corrupt file self-heals.
/// </summary>
public sealed class PlaybackHistoryStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-playback-tests-" + Guid.NewGuid());
    private readonly List<PlaybackHistoryStore> _stores = [];
    private DateTime _now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private string DataDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif");

    private PlaybackHistoryStore Build(IFileSystem? fileSystem = null, ILogger<PlaybackHistoryStore>? logger = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new PlaybackHistoryStore(
            paths.Object,
            fileSystem ?? new FileSystem(),
            logger ?? NullLogger<PlaybackHistoryStore>.Instance,
            () => _now,
            (_, _) => Task.CompletedTask);
        _stores.Add(store);
        return store;
    }

    private static PlaybackEvent Event(Guid userId, Guid itemId, DateTime ts, bool completed, string kind = "Movie", Guid? seriesId = null)
        => new(ts, userId, itemId, kind, seriesId, "Item " + itemId.ToString("N")[..4], seriesId is null ? null : "Series", 0, 0, completed);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met in time");
    }

    [Fact]
    public async Task Record_IsDrained_AndFoundByGetWeek_WithinTheWindow()
    {
        var store = Build();
        store.Start();
        var user = Guid.NewGuid();
        var ev = Event(user, Guid.NewGuid(), _now, completed: true);

        store.Record(ev);
        await WaitFor(() => store.GetWeek(user, _now.AddDays(-7)).Count == 1);

        Assert.Single(store.GetWeek(user, _now.AddDays(-7)));
        Assert.Empty(store.GetWeek(user, _now.AddMinutes(1)));
        Assert.Empty(store.GetWeek(Guid.NewGuid(), _now.AddDays(-7)));
        await store.StopAsync();
    }

    [Fact]
    public async Task Rollup_IsIncrementedAtWriteTime()
    {
        var store = Build();
        store.Start();
        var user = Guid.NewGuid();

        store.Record(Event(user, Guid.NewGuid(), _now, completed: true));
        store.Record(Event(user, Guid.NewGuid(), _now, completed: true));
        store.Record(Event(user, Guid.NewGuid(), _now, completed: false));
        await WaitFor(() => store.GetYearToDate(user, 2026).Total == 3);

        var ytd = store.GetYearToDate(user, 2026);
        Assert.Equal(3, ytd.Total);
        Assert.Equal(2, ytd.Completed);
        await store.StopAsync();
    }

    [Fact]
    public async Task Rollup_CountsADuplicatePlaybackStopOnce()
    {
        var store = Build();
        store.Start();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        // ISessionManager.PlaybackStopped can fire twice for one stop.
        store.Record(Event(user, item, _now, completed: true));
        store.Record(Event(user, item, _now.AddSeconds(1), completed: true));
        await WaitFor(() => store.GetYearToDate(user, 2026).Total == 1);

        Assert.Equal(1, store.GetYearToDate(user, 2026).Total);
        Assert.Equal(1, store.GetYearToDate(user, 2026).Completed);
        Assert.Single(store.GetWeek(user, _now.AddDays(-7)));
        await store.StopAsync();
    }

    [Fact]
    public async Task Rollup_CountsARewatchOnAnotherDayTwice()
    {
        var store = Build();
        store.Start();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        store.Record(Event(user, item, _now.AddDays(-2), completed: true));
        store.Record(Event(user, item, _now, completed: true));
        await WaitFor(() => store.GetYearToDate(user, 2026).Total == 2);

        Assert.Equal(2, store.GetYearToDate(user, 2026).Total);
        await store.StopAsync();
    }

    [Fact]
    public async Task Duplicate_KeepsCompletion_WhenEitherStopCompleted()
    {
        var store = Build();
        store.Start();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        store.Record(Event(user, item, _now, completed: false));
        store.Record(Event(user, item, _now.AddSeconds(1), completed: true));
        await WaitFor(() => store.GetYearToDate(user, 2026).Total == 1);

        Assert.Equal(1, store.GetYearToDate(user, 2026).Completed);
        Assert.True(store.GetWeek(user, _now.AddDays(-7))[0].Completed);
        await store.StopAsync();
    }

    [Fact]
    public async Task Start_CollapsesLegacySameDayDuplicates_AndFixesTheRollup()
    {
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var ts = _now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(
            Path.Combine(DataDir, "playback-history.json"),
            $$"""
            { "Schema": 1, "TrackingSinceUtc": "{{ts}}",
              "Rollup": { "{{user:N}}:2026": { "Total": 3, "Completed": 3, "ByMonth": { "06": { "Total": 3, "Completed": 3 } } } },
              "Events": [
                { "Ts": "{{ts}}", "UserId": "{{user}}", "ItemId": "{{item}}", "Kind": "Episode", "Name": "e", "PositionTicks": 0, "RuntimeTicks": 0, "Completed": true },
                { "Ts": "{{ts}}", "UserId": "{{user}}", "ItemId": "{{item}}", "Kind": "Episode", "Name": "e", "PositionTicks": 0, "RuntimeTicks": 0, "Completed": true },
                { "Ts": "{{ts}}", "UserId": "{{user}}", "ItemId": "{{item}}", "Kind": "Episode", "Name": "e", "PositionTicks": 0, "RuntimeTicks": 0, "Completed": true } ] }
            """);

        var store = Build();
        store.Start();

        await WaitFor(() => store.GetYearToDate(user, 2026).Total == 1);
        Assert.Single(store.GetWeek(user, _now.AddDays(-7)));
        await store.StopAsync();
    }

    [Fact]
    public async Task GetWeek_DeduplicatesByItem_KeepingTheLatestEvent()
    {
        var store = Build();
        store.Start();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();

        store.Record(Event(user, item, _now.AddHours(-2), completed: false)); // partial first
        store.Record(Event(user, item, _now, completed: true));               // finished later
        await WaitFor(() => store.GetWeek(user, _now.AddDays(-7)).Count == 1);

        var entry = Assert.Single(store.GetWeek(user, _now.AddDays(-7)));
        Assert.True(entry.Completed);
        await store.StopAsync();
    }

    [Fact]
    public async Task Events_PersistAcrossInstances()
    {
        var first = Build();
        first.Start();
        var user = Guid.NewGuid();
        first.Record(Event(user, Guid.NewGuid(), _now, completed: true));
        await WaitFor(() => first.GetWeek(user, _now.AddDays(-7)).Count == 1);
        await first.StopAsync();

        var second = Build();
        Assert.Single(second.GetWeek(user, _now.AddDays(-7)));
        Assert.Equal(1, second.GetYearToDate(user, 2026).Total);
    }

    [Fact]
    public async Task Compact_DropsOldEvents_ButKeepsTheRollup()
    {
        var store = Build();
        store.Start();
        var user = Guid.NewGuid();
        store.Record(Event(user, Guid.NewGuid(), _now.AddDays(-120), completed: true)); // January-ish
        store.Record(Event(user, Guid.NewGuid(), _now.AddDays(-1), completed: true));
        await WaitFor(() => store.GetYearToDate(user, 2026).Total == 2);

        _now = _now.AddMonths(1); // roll the month so Compact is not a no-op
        store.Compact();

        Assert.Single(store.GetWeek(user, DateTime.MinValue)); // only the recent event remains
        Assert.Equal(2, store.GetYearToDate(user, 2026).Total); // rollup untouched
        await store.StopAsync();
    }

    [Fact]
    public void Compact_IsANoOpWithinTheSameMonth_NoDiskWrite()
    {
        var counting = new CountingFileSystem();
        var store = Build(counting);
        store.Start();     // stamps TrackingSinceUtc
        store.Compact();   // first compaction: stamps LastCompactUtc for this month
        var writesBefore = counting.Writes;

        store.Compact();   // same month
        store.Compact();

        Assert.Equal(writesBefore, counting.Writes);
    }

    [Fact]
    public void Construction_WhenFileIsCorrupt_StartsEmpty_LogsError()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "playback-history.json"), "{ not json");
        var logger = new Mock<ILogger<PlaybackHistoryStore>>();

        var store = Build(logger: logger.Object);

        Assert.Empty(store.GetWeek(Guid.NewGuid(), DateTime.MinValue));
        logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Start_RebuildsTheRollup_WhenItIsMissingButEventsExist()
    {
        var user = Guid.NewGuid();
        Directory.CreateDirectory(DataDir);
        // A file with events but an empty rollup (as if the rollup key was hand-deleted).
        var ts = _now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        File.WriteAllText(
            Path.Combine(DataDir, "playback-history.json"),
            $$"""
            { "Schema": 1, "Rollup": {}, "TrackingSinceUtc": "{{ts}}",
              "Events": [ { "Ts": "{{ts}}", "UserId": "{{user}}", "ItemId": "{{Guid.NewGuid()}}",
                            "Kind": "Movie", "Name": "x", "PositionTicks": 0, "RuntimeTicks": 0, "Completed": true } ] }
            """);

        var store = Build();
        store.Start();

        await WaitFor(() => store.GetYearToDate(user, 2026).Total == 1);
        Assert.Equal(1, store.GetYearToDate(user, 2026).Completed);
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

    /// <summary>Wraps a real <see cref="FileSystem"/> and counts atomic writes (Move calls).</summary>
    private sealed class CountingFileSystem : IFileSystem
    {
        private readonly FileSystem _inner = new();

        public int Writes { get; private set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            Writes++;
            _inner.Move(sourceFileName, destFileName, overwrite);
        }

        public void Delete(string path) => _inner.Delete(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern) => _inner.EnumerateFiles(path, searchPattern);

        public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);
    }
}
