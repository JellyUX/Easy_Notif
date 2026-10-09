using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// Covers <see cref="ScheduledAnnouncementRunner"/> with the real <see cref="ManualEmailService"/>
/// and store: an announcement goes out once when due (never before, never again), users who opted
/// out of announcements are left out of a send to everyone, the admin's choice wins in "selected"
/// mode without an unsubscribe header, and transient failures are retried on the same keys, then
/// abandoned with an incident.
/// </summary>
public sealed class ScheduledAnnouncementRunnerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid OptedOut = Guid.NewGuid();

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-announcement-runner-" + Guid.NewGuid());
    private readonly HashSet<string> _usedKeys = [];
    private readonly Mock<IEmailSender> _sender = new();
    private readonly Mock<IQuotaGuard> _quota = new();
    private readonly FakeEasyNotifLog _log = new();
    private readonly ScheduledAnnouncementStore _store;
    private readonly FakeSecretStore _secrets = new("re_key", null, "unsub-secret-0123456789");
    private DateTime _now = T0;
    private Func<EmailMessage, SendResult> _respond = _ => new SendResult(true, "re_1", 200, null);

    public ScheduledAnnouncementRunnerTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        _store = new ScheduledAnnouncementStore(paths.Object, new FileSystem(), NullLogger<ScheduledAnnouncementStore>.Instance);
        _quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));

        // Same contract as Resend: a reused idempotency key never delivers again (409).
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>((m, _) =>
            {
                Sent.Add(m);
                if (_usedKeys.Contains(m.IdempotencyKey!))
                {
                    return Task.FromResult(new SendResult(false, null, 409, "duplicate") { Deduplicated = true });
                }

                var result = _respond(m);
                if (result.Success)
                {
                    _usedKeys.Add(m.IdempotencyKey!);
                    Delivered.Add(m.To);
                }

                return Task.FromResult(result);
            });
    }

    private List<EmailMessage> Sent { get; } = [];

    private List<string> Delivered { get; } = [];

    private ScheduledAnnouncementRunner Build()
    {
        var everyone = new[]
        {
            new Recipient(Alice, "alice@example.org"),
            new Recipient(Bob, "bob@example.org"),
            new Recipient(OptedOut, "optout@example.org")
        };
        var preferences = new Mock<IPreferenceService>();
        preferences.Setup(p => p.GetContactable(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<bool>()))
            .Returns<IReadOnlyCollection<Guid>?, bool>((ids, excludeOptOuts) =>
                [.. everyone.Where(r => (ids is null || ids.Contains(r.UserId)) && !(excludeOptOuts && r.UserId == OptedOut))]);
        preferences.Setup(p => p.GetAllForAdmin()).Returns([]);

        var config = new FakeConfigAccessor(new PluginConfiguration { FromEmail = "news@example.org", PublicServerUrl = "https://media.example.org" });
        var manual = new ManualEmailService(
            _sender.Object,
            preferences.Object,
            config,
            _secrets,
            _quota.Object,
            new Mock<ISendLog>().Object,
            TestTemplateStore.Create(),
            NullLogger<ManualEmailService>.Instance);
        return new ScheduledAnnouncementRunner(_store, manual, _quota.Object, config, _secrets, _log, () => _now);
    }

    private ScheduledAnnouncement Schedule(string mode = "all", IEnumerable<Guid>? userIds = null)
    {
        var announcement = new ScheduledAnnouncement
        {
            Id = Guid.NewGuid(),
            Subject = "Movie night",
            Text = "Friday at 9",
            Wrap = true,
            Mode = mode,
            UserIds = [.. userIds ?? []],
            SendAtUtc = T0.AddMinutes(20),
            CreatedUtc = T0
        };
        _store.Add(announcement);
        return announcement;
    }

    [Fact]
    public async Task GoesOutOnce_WhenDue_NotBefore_NotAgain()
    {
        var runner = Build();
        var announcement = Schedule();

        await runner.RunDueAsync(CancellationToken.None);
        Assert.Empty(Sent);

        _now = T0.AddMinutes(30);
        await runner.RunDueAsync(CancellationToken.None);
        _now = T0.AddMinutes(45);
        await runner.RunDueAsync(CancellationToken.None);

        Assert.Equal(2, Sent.Count);
        Assert.All(Sent, m => Assert.StartsWith($"manual:{announcement.Id:N}:", m.IdempotencyKey, StringComparison.Ordinal));
        var stored = Assert.Single(_store.All());
        Assert.Equal(ScheduledAnnouncement.Sent, stored.Status);
        Assert.Equal(2, stored.SentCount);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(T0.AddMinutes(30), stored.FinishedUtc);
    }

    [Fact]
    public async Task All_LeavesOutAnnouncementOptOuts_AndCarriesTheUnsubscribeLink()
    {
        var runner = Build();
        Schedule();
        _now = T0.AddMinutes(30);

        await runner.RunDueAsync(CancellationToken.None);

        Assert.Equal(["alice@example.org", "bob@example.org"], Sent.Select(m => m.To).Order());
        Assert.All(Sent, m =>
        {
            Assert.NotNull(m.Headers);
            Assert.Contains("https://media.example.org/EasyNotif/u/", m.Html, StringComparison.Ordinal);
            Assert.Contains("Movie night</div>", m.Html, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Selected_TheAdminChoiceWins_WithoutUnsubscribe()
    {
        var runner = Build();
        Schedule("selected", [OptedOut]);
        _now = T0.AddMinutes(30);

        await runner.RunDueAsync(CancellationToken.None);

        var message = Assert.Single(Sent);
        Assert.Equal("optout@example.org", message.To);
        Assert.Null(message.Headers);
        Assert.DoesNotContain("/EasyNotif/u/", message.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledAnnouncement_IsNeverSent()
    {
        var runner = Build();
        var announcement = Schedule();
        _store.Cancel(announcement.Id, T0);
        _now = T0.AddHours(1);

        await runner.RunDueAsync(CancellationToken.None);

        Assert.Empty(Sent);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedOnTheSameKeys_WithoutDuplicates()
    {
        var runner = Build();
        Schedule();
        _respond = m => m.To == "bob@example.org" ? new SendResult(false, null, 503, "unavailable") : new SendResult(true, "re_ok", 200, null);
        _now = T0.AddMinutes(30);

        await runner.RunDueAsync(CancellationToken.None);

        var retrying = Assert.Single(_store.All());
        Assert.Equal(ScheduledAnnouncement.Retrying, retrying.Status);
        Assert.Equal([Bob], retrying.RetryUserIds);
        Assert.Equal(_now + DispatchService.RetryBackoff[0], retrying.NextAttemptUtc);

        _respond = _ => new SendResult(true, "re_ok", 200, null);
        _now = retrying.NextAttemptUtc!.Value;
        await runner.RunDueAsync(CancellationToken.None);

        var done = Assert.Single(_store.All());
        Assert.Equal(ScheduledAnnouncement.Sent, done.Status);
        Assert.Equal(2, done.SentCount);
        Assert.Equal(0, done.FailedCount);
        Assert.Equal(3, Sent.Count);
        Assert.Equal(["alice@example.org", "bob@example.org"], Delivered.Order());
    }

    [Fact]
    public async Task PermanentFailure_IsNotRetried()
    {
        var runner = Build();
        Schedule();
        _respond = m => m.To == "bob@example.org" ? new SendResult(false, null, 422, "invalid") : new SendResult(true, "re_ok", 200, null);
        _now = T0.AddMinutes(30);

        await runner.RunDueAsync(CancellationToken.None);

        var done = Assert.Single(_store.All());
        Assert.Equal(ScheduledAnnouncement.Sent, done.Status);
        Assert.Equal(1, done.SentCount);
        Assert.Equal(1, done.FailedCount);
    }

    [Fact]
    public async Task TransientFailures_AreAbandonedAfterTheFifthAttempt_WithAnIncident()
    {
        var runner = Build();
        Schedule();
        _respond = m => m.To == "bob@example.org" ? new SendResult(false, null, 0, "timeout") : new SendResult(true, "re_ok", 200, null);
        _now = T0.AddMinutes(30);

        for (var pass = 0; pass < 5; pass++)
        {
            await runner.RunDueAsync(CancellationToken.None);
            _now = _store.All()[0].NextAttemptUtc ?? _now;
        }

        var done = Assert.Single(_store.All());
        Assert.Equal(ScheduledAnnouncement.Sent, done.Status);
        Assert.Equal(5, done.Attempts);
        Assert.Equal(1, done.FailedCount);
        Assert.Contains(_log.Entries, e => e.EventType == "announcement.retry-abandoned" && e.Level == "Warn");
        Assert.Contains("announcement.retry-abandoned", Jellyfin.Plugin.EasyNotif.Alerts.IncidentStore.Types);
    }

    [Fact]
    public async Task QuotaOverOrNoTransport_KeepsTheAnnouncementDue()
    {
        var runner = Build();
        Schedule();
        _now = T0.AddMinutes(30);
        _quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(3000, 100, 3000, 100, true, true));

        await runner.RunDueAsync(CancellationToken.None);

        Assert.Empty(Sent);
        Assert.Single(_store.Due(_now));
        Assert.Contains(_log.Entries, e => e.EventType == "announcement.skipped");

        _quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
        _secrets.ResendApiKey = null;
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Empty(Sent);

        _secrets.ResendApiKey = "re_key";
        await runner.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, Sent.Count);
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
