using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// The audit PERF-04 acceptance scenario: 20 users following a series, 5 flushes in the day and a
/// newsletter due the same evening, over one real <see cref="QuotaGuard"/> shared by the series
/// notifier and the dispatch engine. The daily limit is lowered to 50 so the reserve matters: the
/// series emails alone would reach 60 (3 per user), the reserve stops them at 30, and the newsletter
/// still reaches all 20 recipients.
/// </summary>
public sealed class QuotaBudgetScenarioTests : IDisposable
{
    private const int Users = 20;
    private const int DailyLimit = 50;

    private readonly SeriesNotifierHarness _h = new();
    private readonly string _quotaDir = Path.Combine(Path.GetTempPath(), "enotif-quota-scenario-" + Guid.NewGuid());
    private readonly QuotaGuard _quota;

    public QuotaBudgetScenarioTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_quotaDir);
        _quota = new QuotaGuard(
            paths.Object,
            new FileSystem(),
            new FakeConfigAccessor(new PluginConfiguration { QuotaDailyLimit = DailyLimit }),
            NullLogger<QuotaGuard>.Instance,
            new FakeEasyNotifLog(),
            () => new DateTimeOffset(_h.Now));
        _h.Quota.Setup(q => q.Snapshot()).Returns(() => _quota.Snapshot());
        _h.Quota.Setup(q => q.RecordSend()).Callback(() => _quota.RecordSend());
    }

    [Fact]
    public async Task NewsletterDueToday_IsSentInFull_AfterFiveSeriesFlushes()
    {
        var newsletterAt = SeriesNotifierHarness.T0.Date.AddHours(18);
        _h.Campaigns.Campaigns.Add(new Campaign
        {
            Id = "newsletter",
            Type = CampaignType.Newsletter,
            Category = EmailCategory.News,
            Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(18, 0)),
            Enabled = true,
            NextRunUtc = newsletterAt
        });
        var series = Guid.NewGuid();
        var users = Enumerable.Range(0, Users).Select(i => _h.Follow(series, $"user{i}@example.org")).ToList();

        for (var flush = 1; flush <= 5; flush++)
        {
            _h.Add(series, 1, flush);
            await _h.RunTicksAsync(TimeSpan.FromHours(1));
        }

        Assert.Equal(DailyLimit - 20, _h.Sent.Count);
        Assert.Contains(_h.Log.Entries, e => e.EventType == "series.skipped" && (string?)e.Fields?["reason"] == "quota-reserve");

        _h.Now = newsletterAt;
        var newsletter = new List<EmailMessage>();
        var dispatch = BuildDispatch(users, newsletter);
        await dispatch.RunDueAsync(CancellationToken.None);

        Assert.Equal(Users, newsletter.Count);
        Assert.Equal(DailyLimit, _quota.Snapshot().DailyToday);
        Assert.True(_h.Campaigns.Get("newsletter")!.NextRunUtc > newsletterAt);
    }

    private DispatchService BuildDispatch(List<Guid> users, List<EmailMessage> sent)
    {
        var preferences = new Mock<IPreferenceService>();
        preferences.Setup(p => p.GetRecipients(It.IsAny<EmailCategory>()))
            .Returns([.. users.Select(id => new Recipient(id, $"user-{id:N}@example.org"))]);
        preferences.Setup(p => p.GetAllForAdmin()).Returns([]);
        var composer = new Mock<IEmailComposer>();
        composer.Setup(c => c.PrepareAsync(It.IsAny<Campaign>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PreparedCampaign
            {
                ShouldSend = true,
                Render = _ => new EmailContent("Newsletter", "<p>Body</p>", "Body")
            });
        var sender = new Mock<IEmailSender>();
        sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>((m, _) =>
            {
                sent.Add(m);
                return Task.FromResult(new SendResult(true, "re_" + sent.Count, 200, null));
            });

        return new DispatchService(
            _h.Campaigns,
            new FakeRetryQueueStore(),
            preferences.Object,
            composer.Object,
            sender.Object,
            _quota,
            new Mock<ISendLog>().Object,
            new FakeConfigAccessor(new PluginConfiguration { ResendApiKey = "re_key", FromEmail = "from@example.org" }),
            new FakeSecretStore(resendApiKey: "re_key"),
            new FakeEasyNotifLog(),
            NullLogger<DispatchService>.Instance,
            () => _h.Now);
    }

    public void Dispose()
    {
        _quota.Dispose();
        _h.Dispose();
        if (Directory.Exists(_quotaDir))
        {
            Directory.Delete(_quotaDir, recursive: true);
        }
    }
}
