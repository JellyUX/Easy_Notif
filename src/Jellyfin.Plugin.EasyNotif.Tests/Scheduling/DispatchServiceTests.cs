using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// Covers <see cref="DispatchService"/>: an empty tick touches no store, a due campaign resolves
/// opt-in recipients and stamps an idempotency key, the semaphore blocks a concurrent run, a single
/// send failure does not stop the run, an error or a quota stop leaves <c>NextRunUtc</c> unchanged
/// so the next tick retries, and run-now works on a disabled campaign.
/// </summary>
public sealed class DispatchServiceTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public FakeCampaignStore Campaigns { get; }

        public Mock<IPreferenceService> Preferences { get; } = new();

        public Mock<IEmailComposer> Composer { get; } = new();

        public Mock<IEmailSender> Sender { get; } = new();

        public Mock<IQuotaGuard> Quota { get; } = new();

        public Mock<ISendLog> SendLog { get; } = new();

        public FakeConfigAccessor Config { get; }

        public FakeEasyNotifLog Log { get; } = new();

        public List<EmailMessage> Sent { get; } = [];

        public DispatchService Service { get; }

        public Harness(
            IEnumerable<Campaign>? campaigns = null,
            PluginConfiguration? config = null,
            bool sendSucceeds = true,
            Func<EmailMessage, Task>? onSend = null)
        {
            Campaigns = new FakeCampaignStore((campaigns ?? [DueNewsletter()]).ToArray());
            Config = new FakeConfigAccessor(config ?? new PluginConfiguration
            {
                ResendApiKey = "re_key",
                FromEmail = "from@example.org",
                ReplyTo = "reply@example.org"
            });

            Composer.Setup(c => c.PrepareAsync(It.IsAny<Campaign>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(new PreparedCampaign
                {
                    ShouldSend = true,
                    Render = _ => new EmailContent("Subject", "<p>Body</p>", "Body")
                });

            Sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
                .Returns<EmailMessage, CancellationToken>(async (m, _) =>
                {
                    Sent.Add(m);
                    if (onSend is not null)
                    {
                        await onSend(m).ConfigureAwait(false);
                    }

                    return sendSucceeds
                        ? new SendResult(true, "re_" + Sent.Count, 200, null)
                        : new SendResult(false, null, 422, "rejected");
                });

            Quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
            Preferences.Setup(p => p.GetRecipients(It.IsAny<EmailCategory>())).Returns([]);
            Preferences.Setup(p => p.GetAllForAdmin()).Returns([]);

            Service = new DispatchService(
                Campaigns,
                Preferences.Object,
                Composer.Object,
                Sender.Object,
                Quota.Object,
                SendLog.Object,
                Config,
                Log,
                NullLogger<DispatchService>.Instance,
                () => Now);
        }

        public void Recipients(params Guid[] userIds)
            => Preferences.Setup(p => p.GetRecipients(It.IsAny<EmailCategory>()))
                .Returns(userIds.Select(id => new Recipient(id, $"user-{id:N}@example.org")).ToList());

        public void ExcludedNoEmail(EmailCategory category, int count)
            => Preferences.Setup(p => p.GetAllForAdmin()).Returns(
                Enumerable.Range(0, count)
                    .Select(_ => new AdminPreferenceRow(
                        Guid.NewGuid(), "x", "(none)", false,
                        new Dictionary<EmailCategory, bool> { [category] = true }, null))
                    .ToList());
    }

    private static Campaign DueNewsletter() => new()
    {
        Id = "newsletter",
        Type = CampaignType.Newsletter,
        Category = EmailCategory.News,
        Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Friday, new TimeOnly(9, 0)),
        Enabled = true,
        NextRunUtc = Now.AddMinutes(-1)
    };

    [Fact]
    public async Task RunDueAsync_WhenNothingIsDue_TouchesNoStore()
    {
        var harness = new Harness([new Campaign { Id = "newsletter", Enabled = true, NextRunUtc = Now.AddHours(1), Schedule = RecurrenceSchedule.Daily(new TimeOnly(9, 0)) }]);

        await harness.Service.RunDueAsync(CancellationToken.None);

        harness.Preferences.Verify(p => p.GetRecipients(It.IsAny<EmailCategory>()), Times.Never);
        harness.Preferences.Verify(p => p.GetAllForAdmin(), Times.Never);
        harness.Sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDueAsync_DisabledCampaign_IsNeverRun()
    {
        var harness = new Harness([new Campaign { Id = "newsletter", Enabled = false, NextRunUtc = Now.AddMinutes(-5), Schedule = RecurrenceSchedule.Daily(new TimeOnly(9, 0)) }]);

        await harness.Service.RunDueAsync(CancellationToken.None);

        harness.Sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunDueAsync_DueCampaign_SendsToEachOptInRecipient_WithIdempotencyKeyAndTags()
    {
        var harness = new Harness();
        harness.Recipients(Alice, Bob, Carol);

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Equal(3, harness.Sent.Count);
        var msg = harness.Sent[0];
        Assert.StartsWith("newsletter:202606151159:", msg.IdempotencyKey); // slot = NextRunUtc (Now - 1 min)
        Assert.Contains(msg.Tags!, t => t is { Name: "campaignId", Value: "newsletter" });
        Assert.Contains(msg.Tags!, t => t is { Name: "category", Value: "News" });

        var campaign = harness.Campaigns.Get("newsletter")!;
        Assert.Equal(Now, campaign.LastSentUtc);
        Assert.True(campaign.NextRunUtc > Now);
    }

    [Fact]
    public async Task RunDueAsync_NotDueCampaign_LeavesNextRunUnchanged()
    {
        var future = Now.AddDays(2);
        var harness = new Harness([new Campaign { Id = "newsletter", Enabled = true, NextRunUtc = future, Schedule = RecurrenceSchedule.Daily(new TimeOnly(9, 0)) }]);

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Equal(future, harness.Campaigns.Get("newsletter")!.NextRunUtc);
    }

    [Fact]
    public async Task RunDueAsync_ConcurrentRun_DoesNotDoubleSend()
    {
        var gate = new TaskCompletionSource();
        var harness = new Harness(onSend: _ => gate.Task);
        harness.Recipients(Alice, Bob);

        var first = harness.Service.RunDueAsync(CancellationToken.None);
        var second = harness.Service.RunDueAsync(CancellationToken.None);
        await second; // returns immediately: the gate is taken
        gate.SetResult();
        await first;

        Assert.Equal(2, harness.Sent.Count);
    }

    [Fact]
    public async Task RunDueAsync_OneSendFails_TheRunContinues_AndStillAdvances()
    {
        var calls = 0;
        var harness = new Harness(onSend: _ => { calls++; return Task.CompletedTask; });
        harness.Sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>((m, _) =>
            {
                harness.Sent.Add(m);
                var n = ++calls;
                return Task.FromResult(n == 1
                    ? new SendResult(false, null, 422, "bad")
                    : new SendResult(true, "re_" + n, 200, null));
            });
        harness.Recipients(Alice, Bob, Carol);

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Equal(3, harness.Sent.Count);
        harness.Quota.Verify(q => q.RecordSend(), Times.Exactly(2));
        Assert.True(harness.Campaigns.Get("newsletter")!.NextRunUtc > Now);
    }

    [Fact]
    public async Task RunDueAsync_RecipientResolutionThrows_IsSwallowed_NextRunUnchanged()
    {
        var harness = new Harness();
        var original = harness.Campaigns.Get("newsletter")!.NextRunUtc;
        harness.Preferences.Setup(p => p.GetRecipients(It.IsAny<EmailCategory>())).Throws(new IOException("disk gone"));

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Equal(original, harness.Campaigns.Get("newsletter")!.NextRunUtc);
        Assert.Contains(harness.Log.Entries, e => e.EventType == "dispatch.error");
    }

    [Fact]
    public async Task RunDueAsync_QuotaOverAtStart_SkipsAndKeepsNextRun()
    {
        var harness = new Harness();
        var original = harness.Campaigns.Get("newsletter")!.NextRunUtc;
        harness.Quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(3000, 100, 3000, 100, true, true));
        harness.Recipients(Alice, Bob);

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Empty(harness.Sent);
        Assert.Equal(original, harness.Campaigns.Get("newsletter")!.NextRunUtc);
        Assert.Contains(harness.Log.Entries, e => e.EventType == "dispatch.skipped" && (string?)e.Fields?["reason"] == "quota-over");
    }

    [Fact]
    public async Task RunDueAsync_QuotaTripsMidRun_StopsSending_KeepsNextRun()
    {
        var harness = new Harness();
        var original = harness.Campaigns.Get("newsletter")!.NextRunUtc;
        var checks = 0;
        harness.Quota.Setup(q => q.Snapshot()).Returns(() =>
        {
            checks++;
            // check 1 = the pre-run guard, checks 2 and 3 = the first two loop iterations (pass),
            // check 4 = the third iteration, which stops the run.
            var over = checks > 3;
            return new QuotaSnapshot(over ? 100 : 0, over ? 100 : 0, 3000, 100, over, over);
        });
        harness.Recipients(Alice, Bob, Carol);

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Equal(2, harness.Sent.Count);
        Assert.Equal(original, harness.Campaigns.Get("newsletter")!.NextRunUtc);
        Assert.Contains(harness.Log.Entries, e => e.EventType == "dispatch.skipped" && (string?)e.Fields?["reason"] == "quota-over-mid-run");
    }

    [Fact]
    public async Task RunDueAsync_TransportNotConfigured_SkipsAndKeepsNextRun()
    {
        var harness = new Harness(config: new PluginConfiguration { FromEmail = "from@example.org" }); // no ResendApiKey
        var original = harness.Campaigns.Get("newsletter")!.NextRunUtc;
        harness.Recipients(Alice);

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Empty(harness.Sent);
        Assert.Equal(original, harness.Campaigns.Get("newsletter")!.NextRunUtc);
        Assert.Contains(harness.Log.Entries, e => e.EventType == "dispatch.skipped" && (string?)e.Fields?["reason"] == "transport-not-configured");
    }

    [Fact]
    public async Task RunCampaignNowAsync_RunsADisabledCampaign_AndAdvancesIt()
    {
        var harness = new Harness([new Campaign
        {
            Id = "weekly-recap",
            Type = CampaignType.WeeklyRecap,
            Category = EmailCategory.Recap,
            Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(8, 0)),
            Enabled = false,
            NextRunUtc = null
        }]);
        harness.Recipients(Alice, Bob);

        var result = await harness.Service.RunCampaignNowAsync("weekly-recap", CancellationToken.None);

        Assert.True(result.Found);
        Assert.Equal(2, result.Sent);
        Assert.NotNull(harness.Campaigns.Get("weekly-recap")!.NextRunUtc);
    }

    [Fact]
    public async Task RunCampaignNowAsync_UnknownId_ReturnsNotFound()
    {
        var result = await new Harness().Service.RunCampaignNowAsync("nope", CancellationToken.None);

        Assert.False(result.Found);
    }

    [Fact]
    public async Task RunDueAsync_LogsResolvedCounts()
    {
        var harness = new Harness();
        harness.Recipients(Alice, Bob);
        harness.ExcludedNoEmail(EmailCategory.News, 3);

        await harness.Service.RunDueAsync(CancellationToken.None);

        var resolved = Assert.Single(harness.Log.Entries, e => e.EventType == "dispatch.resolved");
        Assert.Equal(2, resolved.Fields!["eligible"]);
        Assert.Equal(3, resolved.Fields!["excludedNoEmail"]);
    }

    [Fact]
    public async Task RunDueAsync_WithPublicUrlAndSecret_StampsAVerifiableListUnsubscribeHeader()
    {
        var harness = new Harness(config: new PluginConfiguration
        {
            ResendApiKey = "re_key",
            FromEmail = "from@example.org",
            PublicServerUrl = "https://media.example.org",
            UnsubscribeSecret = "unsub-secret-0123456789"
        });
        harness.Recipients(Alice);

        await harness.Service.RunDueAsync(CancellationToken.None);

        var header = harness.Sent[0].Headers!["List-Unsubscribe"];
        var token = header.Split("/u/")[1].TrimEnd('>');
        Assert.True(UnsubscribeToken.TryVerify("unsub-secret-0123456789", token, out var userId, out var category));
        Assert.Equal(Alice, userId);
        Assert.Equal("news", category);
    }

    [Fact]
    public async Task RunDueAsync_WithoutPublicUrl_OmitsTheHeader()
    {
        var harness = new Harness();
        harness.Recipients(Alice);

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Null(harness.Sent[0].Headers);
    }

    [Fact]
    public async Task RunDueAsync_EmptyDigest_DoesNotSend_ButAdvancesNextRun()
    {
        var harness = new Harness();
        harness.Recipients(Alice, Bob);
        harness.Composer.Setup(c => c.PrepareAsync(It.IsAny<Campaign>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PreparedCampaign
            {
                ShouldSend = false,
                SkipReason = "empty-digest",
                Render = _ => new EmailContent("x", null, null)
            });

        await harness.Service.RunDueAsync(CancellationToken.None);

        Assert.Empty(harness.Sent);
        harness.Preferences.Verify(p => p.GetRecipients(It.IsAny<EmailCategory>()), Times.Never);
        var campaign = harness.Campaigns.Get("newsletter")!;
        Assert.Equal(Now, campaign.LastSentUtc);
        Assert.True(campaign.NextRunUtc > Now);
        Assert.Contains(harness.Log.Entries, e => e.EventType == "dispatch.campaign" && (string?)e.Fields?["skipped"] == "empty-digest");
    }

    [Fact]
    public async Task PreviewAsync_SendsOneMailToTheGivenAddress_WithoutAdvancing()
    {
        var harness = new Harness();
        harness.Composer.Setup(c => c.PrepareAsync(It.IsAny<Campaign>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new PreparedCampaign
            {
                ShouldSend = true,
                Render = _ => new EmailContent("Newsletter", "<p>b</p>", "b"),
                MovieCount = 2,
                SeriesCount = 1
            });
        SendLogEntry? logged = null;
        harness.SendLog.Setup(l => l.Append(It.IsAny<SendLogEntry>())).Callback<SendLogEntry>(e => logged = e);
        var before = harness.Campaigns.Get("newsletter")!.NextRunUtc;

        var result = await harness.Service.PreviewAsync("newsletter", "admin@example.org", CancellationToken.None);

        Assert.True(result.Found);
        Assert.True(result.Sent);
        Assert.Equal(2, result.Movies);
        var msg = Assert.Single(harness.Sent);
        Assert.Equal("admin@example.org", msg.To);
        Assert.StartsWith("[Preview]", msg.Subject);
        Assert.Equal(0, harness.Campaigns.UpdateCount);
        Assert.Equal(before, harness.Campaigns.Get("newsletter")!.NextRunUtc);
        Assert.Equal("preview", logged!.Context);
    }

    [Fact]
    public async Task PreviewAsync_UnknownId_ReturnsNotFound()
    {
        var result = await new Harness().Service.PreviewAsync("nope", "admin@example.org", CancellationToken.None);

        Assert.False(result.Found);
    }

    [Fact]
    public async Task RunDueAsync_DeduplicatedSend_IsNotAFailure_AndTheCampaignStillAdvances()
    {
        var harness = new Harness();
        harness.Recipients(Alice, Bob);
        harness.Sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>((m, _) =>
            {
                harness.Sent.Add(m);
                return Task.FromResult(new SendResult(false, null, 409, "idempotency") { Deduplicated = true });
            });

        await harness.Service.RunDueAsync(CancellationToken.None);

        harness.Quota.Verify(q => q.RecordSend(), Times.Never);
        var campaign = harness.Campaigns.Get("newsletter")!;
        Assert.True(campaign.NextRunUtc > Now);
        var log = Assert.Single(harness.Log.Entries, e => e.EventType == "dispatch.campaign");
        Assert.Equal(0, log.Fields!["failed"]);
        Assert.Equal(2, log.Fields!["deduplicated"]);
    }
}
