using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="QuotaGuard"/>: the rolling 30-day window, the exact 80 percent / 100 percent
/// thresholds, the UTC "today" count, persistence between instances and corrupt-file recovery.
/// </summary>
public sealed class QuotaGuardTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-quota-tests-" + Guid.NewGuid());
    private readonly List<QuotaGuard> _guards = [];
    private DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private string DataDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif");

    /// <summary>Gets the <see cref="FakeEasyNotifLog"/> passed to the most recently built guard.</summary>
    public FakeEasyNotifLog EasyNotifLog { get; private set; } = new();

    private QuotaGuard Build(ILogger<QuotaGuard>? logger = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        EasyNotifLog = new FakeEasyNotifLog();
        var guard = new QuotaGuard(paths.Object, new FileSystem(), logger ?? NullLogger<QuotaGuard>.Instance, EasyNotifLog, () => _now);
        _guards.Add(guard);
        return guard;
    }

    [Fact]
    public void Snapshot_WhenEmpty_CountsAreZero()
    {
        var snapshot = Build().Snapshot();

        Assert.Equal(0, snapshot.Last30d);
        Assert.Equal(0, snapshot.DailyToday);
        Assert.False(snapshot.Warn80);
        Assert.False(snapshot.Over);
    }

    [Fact]
    public void RecordSend_CountsEachSendInTheWindow()
    {
        var guard = Build();
        for (var i = 0; i < 5; i++)
        {
            guard.RecordSend();
        }

        Assert.Equal(5, guard.Snapshot().Last30d);
        Assert.Equal(5, guard.Snapshot().DailyToday);
    }

    [Fact]
    public void RecordSend_PurgesEntriesOlderThan30Days()
    {
        var guard = Build();
        guard.RecordSend();

        _now = _now.AddDays(31);
        guard.RecordSend();

        Assert.Equal(1, guard.Snapshot().Last30d);
    }

    [Fact]
    public void Snapshot_KeepsAnEntryAt29Days()
    {
        var guard = Build();
        guard.RecordSend();

        _now = _now.AddDays(29);

        Assert.Equal(1, guard.Snapshot().Last30d);
    }

    [Fact]
    public void Warn80_TogglesExactlyAt2400In30Days()
    {
        var guard = Build();
        SeedOnPriorDays(guard, 2399);
        Assert.False(guard.Snapshot().Warn80);

        guard.RecordSend();
        Assert.True(guard.Snapshot().Warn80);
        Assert.Equal(2400, guard.Snapshot().Last30d);
    }

    [Fact]
    public void Warn80_TogglesExactlyAt80PerDay()
    {
        var guard = Build();
        for (var i = 0; i < 79; i++)
        {
            guard.RecordSend();
        }

        Assert.False(guard.Snapshot().Warn80);
        Assert.Empty(EasyNotifLog.Entries);

        guard.RecordSend();
        Assert.True(guard.Snapshot().Warn80);
        var entry = Assert.Single(EasyNotifLog.Entries);
        Assert.Equal("Warn", entry.Level);
        Assert.Equal("quota.threshold", entry.EventType);
    }

    [Fact]
    public void RecordSend_BelowTheThreshold_NeverLogsAWarning()
    {
        var guard = Build();
        for (var i = 0; i < 50; i++)
        {
            guard.RecordSend();
        }

        Assert.False(guard.Snapshot().Warn80);
        Assert.Empty(EasyNotifLog.Entries);
    }

    [Fact]
    public void Over_TogglesAtTheDailyLimit()
    {
        var guard = Build();
        for (var i = 0; i < 99; i++)
        {
            guard.RecordSend();
        }

        Assert.False(guard.Snapshot().Over);

        guard.RecordSend();
        Assert.True(guard.Snapshot().Over);
    }

    [Fact]
    public void Over_TogglesAtTheMonthlyLimit()
    {
        var guard = Build();
        SeedOnPriorDays(guard, 2999);
        Assert.False(guard.Snapshot().Over);

        guard.RecordSend();
        Assert.True(guard.Snapshot().Over);
    }

    [Fact]
    public void DailyToday_OnlyCountsTheCurrentUtcDay()
    {
        var guard = Build();
        guard.RecordSend();

        _now = _now.AddDays(1);
        guard.RecordSend();

        var snapshot = guard.Snapshot();
        Assert.Equal(2, snapshot.Last30d);
        Assert.Equal(1, snapshot.DailyToday);
    }

    [Fact]
    public void State_PersistsBetweenInstances()
    {
        Build().RecordSend();

        Assert.Equal(1, Build().Snapshot().Last30d);
    }

    [Fact]
    public void Construction_WhenFileIsCorrupt_StartsEmpty_LogsError()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "quota.json"), "{ not json");
        var logger = new Mock<ILogger<QuotaGuard>>();

        var guard = Build(logger.Object);

        Assert.Equal(0, guard.Snapshot().Last30d);
        logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Records <paramref name="count"/> sends spread over days -25 to -2 relative to the current
    /// clock, then restores the clock. Every send lands inside the 30-day window but none on
    /// "today", so the daily count stays zero and a threshold test isolates the 30-day count.
    /// </summary>
    private void SeedOnPriorDays(QuotaGuard guard, int count)
    {
        var anchor = _now;
        try
        {
            for (var i = 0; i < count; i++)
            {
                _now = anchor.AddDays(-25 + (i % 24));
                guard.RecordSend();
            }
        }
        finally
        {
            _now = anchor;
        }
    }

    public void Dispose()
    {
        foreach (var guard in _guards)
        {
            guard.Dispose();
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
