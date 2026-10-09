using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="ScheduledAnnouncementStore"/>: an announcement survives a reload in its own
/// file, only due pending or retrying ones are returned, only a pending one can be cancelled, and
/// finished ones are purged after 30 days.
/// </summary>
public sealed class ScheduledAnnouncementStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-announcements-tests-" + Guid.NewGuid());

    private ScheduledAnnouncementStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        return new ScheduledAnnouncementStore(paths.Object, new FileSystem(), NullLogger<ScheduledAnnouncementStore>.Instance);
    }

    private static ScheduledAnnouncement Announcement(DateTime sendAt, string status = ScheduledAnnouncement.Pending) => new()
    {
        Id = Guid.NewGuid(),
        Subject = "Maintenance",
        Text = "Saturday",
        Wrap = true,
        Lang = "en",
        Mode = "selected",
        UserIds = [Guid.NewGuid()],
        SendAtUtc = sendAt,
        CreatedUtc = T0,
        Status = status
    };

    [Fact]
    public void Add_ThenReload_KeepsEveryField_InItsOwnFile()
    {
        var added = Announcement(T0.AddMinutes(20));
        Build().Add(added);

        var reloaded = Assert.Single(Build().All());

        Assert.Equal(added.Id, reloaded.Id);
        Assert.Equal("Maintenance", reloaded.Subject);
        Assert.True(reloaded.Wrap);
        Assert.Equal("en", reloaded.Lang);
        Assert.Equal(added.UserIds, reloaded.UserIds);
        Assert.Equal(added.SendAtUtc, reloaded.SendAtUtc);
        var json = File.ReadAllText(Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "scheduled-announcements.json"));
        Assert.Contains("\"Schema\": 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IsFinished", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Due_ReturnsPendingAndRetryingWhoseTimeHasCome()
    {
        var store = Build();
        var duePending = Announcement(T0.AddMinutes(-1));
        var laterPending = Announcement(T0.AddMinutes(5));
        var dueRetry = Announcement(T0.AddHours(-2), ScheduledAnnouncement.Retrying);
        dueRetry.NextAttemptUtc = T0;
        var laterRetry = Announcement(T0.AddHours(-2), ScheduledAnnouncement.Retrying);
        laterRetry.NextAttemptUtc = T0.AddMinutes(10);
        var sent = Announcement(T0.AddHours(-1), ScheduledAnnouncement.Sent);
        foreach (var a in new[] { duePending, laterPending, dueRetry, laterRetry, sent })
        {
            store.Add(a);
        }

        var due = store.Due(T0).Select(a => a.Id).ToHashSet();

        Assert.Equal(new HashSet<Guid> { duePending.Id, dueRetry.Id }, due);
    }

    [Fact]
    public void Cancel_OnlyAPendingAnnouncement()
    {
        var store = Build();
        var pending = Announcement(T0.AddMinutes(20));
        var sent = Announcement(T0.AddMinutes(-20), ScheduledAnnouncement.Sent);
        store.Add(pending);
        store.Add(sent);

        Assert.Equal(CancelOutcome.Cancelled, store.Cancel(pending.Id, T0));
        Assert.Equal(CancelOutcome.NotPending, store.Cancel(pending.Id, T0));
        Assert.Equal(CancelOutcome.NotPending, store.Cancel(sent.Id, T0));
        Assert.Equal(CancelOutcome.NotFound, store.Cancel(Guid.NewGuid(), T0));
        Assert.Empty(store.Due(T0.AddHours(1)));
        Assert.Equal(T0, Build().All().Single(a => a.Id == pending.Id).FinishedUtc);
    }

    [Fact]
    public void Purge_RemovesFinishedAnnouncementsAfter30Days_Only()
    {
        var store = Build();
        var old = Announcement(T0.AddDays(-40), ScheduledAnnouncement.Sent);
        old.FinishedUtc = T0.AddDays(-31);
        var recent = Announcement(T0.AddDays(-10), ScheduledAnnouncement.Cancelled);
        recent.FinishedUtc = T0.AddDays(-10);
        var pending = Announcement(T0.AddDays(100));
        foreach (var a in new[] { old, recent, pending })
        {
            store.Add(a);
        }

        store.Purge(T0);

        Assert.Equal(new HashSet<Guid> { recent.Id, pending.Id }, Build().All().Select(a => a.Id).ToHashSet());
    }

    [Fact]
    public void ReturnedAnnouncements_AreDetachedCopies()
    {
        var store = Build();
        store.Add(Announcement(T0));

        store.All()[0].UserIds.Clear();

        Assert.Single(store.All()[0].UserIds);
    }

    public void Dispose()
    {
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
