using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="ManualEmailService"/>: recipient resolution for each mode (preferences
/// ignored, users without an address excluded), the mandatory validations, the List-Unsubscribe
/// header rules, quota/log bookkeeping (masked, only on success) and that one failure does not stop
/// the run.
/// </summary>
public class ManualEmailServiceTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();

    private sealed class Harness
    {
        public List<EmailMessage> Sent { get; } = [];

        public Mock<IEmailSender> Sender { get; } = new();

        public Mock<IPreferenceService> Preferences { get; } = new();

        public Mock<IQuotaGuard> Quota { get; } = new();

        public Mock<ISendLog> SendLog { get; } = new();

        public List<SendLogEntry> LogEntries { get; } = [];

        public FakeConfigAccessor Config { get; }

        public ManualEmailService Service { get; }

        public Harness(PluginConfiguration? config = null, bool sendSucceeds = true)
        {
            Config = new FakeConfigAccessor(config ?? new PluginConfiguration
            {
                ReplyTo = "reply@example.org",
                PublicServerUrl = "https://media.example.org",
                UnsubscribeSecret = "unsub-secret-0123456789"
            });

            Sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
                .Returns<EmailMessage, CancellationToken>((m, _) =>
                {
                    Sent.Add(m);
                    return Task.FromResult(sendSucceeds
                        ? new SendResult(true, "re_" + Sent.Count, 200, null)
                        : new SendResult(false, null, 422, "rejected"));
                });

            SendLog.Setup(l => l.Append(It.IsAny<SendLogEntry>())).Callback<SendLogEntry>(LogEntries.Add);
            Preferences.Setup(p => p.GetContactable(It.IsAny<IReadOnlyCollection<Guid>?>())).Returns([]);
            Preferences.Setup(p => p.GetAllForAdmin()).Returns([]);

            Service = new ManualEmailService(
                Sender.Object,
                Preferences.Object,
                Config,
                Quota.Object,
                SendLog.Object,
                NullLogger<ManualEmailService>.Instance);
        }

        public void Contactable(params Recipient[] recipients)
            => Preferences.Setup(p => p.GetContactable(It.IsAny<IReadOnlyCollection<Guid>?>()))
                .Returns<IReadOnlyCollection<Guid>?>(ids => ids is null
                    ? recipients
                    : recipients.Where(r => ids.Contains(r.UserId)).ToList());

        public void AdminRows(int withoutEmail)
            => Preferences.Setup(p => p.GetAllForAdmin()).Returns(
                Enumerable.Range(0, withoutEmail)
                    .Select(_ => new AdminPreferenceRow(Guid.NewGuid(), "x", "(none)", false, new Dictionary<EmailCategory, bool>(), null))
                    .ToList());
    }

    private static ManualEmailRequest Request(
        string mode = "all",
        string? subject = "Announcement",
        string? html = "<p>Hello</p>",
        string? text = null,
        IReadOnlyList<Guid>? userIds = null,
        string? testAddress = null,
        IReadOnlyList<EmailAttachment>? attachments = null)
        => new(subject!, html, text, mode, userIds, testAddress, attachments);

    [Fact]
    public async Task All_SendsToEveryContactableUser_AndCountsThoseWithoutAnAddress()
    {
        var h = new Harness();
        h.Contactable(new Recipient(Alice, "alice@example.org"), new Recipient(Bob, "bob@example.org"));
        h.AdminRows(withoutEmail: 3);

        var result = await h.Service.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(2, result.Sent);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.SkippedNoEmail);
        Assert.Equal(2, h.Sent.Count);
    }

    [Fact]
    public async Task Selected_SendsOnlyToChosenUsersWithAnAddress()
    {
        var h = new Harness();
        h.Contactable(new Recipient(Alice, "alice@example.org"));

        var result = await h.Service.SendAsync(
            Request(mode: "selected", userIds: [Alice, Bob]),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(1, result.SkippedNoEmail);
        Assert.Equal("alice@example.org", Assert.Single(h.Sent).To);
    }

    [Fact]
    public async Task Test_SendsOnceToTheTestAddress_WithoutAListUnsubscribeHeader()
    {
        var h = new Harness();

        var result = await h.Service.SendAsync(
            Request(mode: "test", testAddress: "me@example.org"),
            CancellationToken.None);

        Assert.Equal(1, result.Sent);
        var message = Assert.Single(h.Sent);
        Assert.Equal("me@example.org", message.To);
        Assert.Null(message.Headers);
        Assert.Equal("manual-test", Assert.Single(h.LogEntries).Context);
    }

    [Fact]
    public async Task HtmlOnly_GeneratesANonEmptyPlainTextPart()
    {
        var h = new Harness();
        h.Contactable(new Recipient(Alice, "alice@example.org"));

        await h.Service.SendAsync(Request(html: "<p>Visible</p>", text: null), CancellationToken.None);

        var text = Assert.Single(h.Sent).Text;
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("<", text!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "<p>x</p>", null)]
    [InlineData("Subject", null, null)]
    public async Task InvalidRequest_Throws(string subject, string? html, string? text)
    {
        var h = new Harness();
        h.Contactable(new Recipient(Alice, "alice@example.org"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.SendAsync(Request(subject: subject, html: html, text: text), CancellationToken.None));
    }

    [Fact]
    public async Task AttachmentsOverTheLimit_Throws()
    {
        var h = new Harness();
        var big = new EmailAttachment { FileName = "big.bin", Content = new byte[ManualEmailService.MaxAttachmentBytes + 1] };

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.SendAsync(Request(attachments: [big]), CancellationToken.None));
    }

    [Fact]
    public async Task RecordsQuotaOncePerSuccess_AndLogsMaskedAddresses()
    {
        var h = new Harness();
        h.Contactable(new Recipient(Alice, "alice@example.org"), new Recipient(Bob, "bob@example.org"));
        h.AdminRows(0);

        await h.Service.SendAsync(Request(), CancellationToken.None);

        h.Quota.Verify(q => q.RecordSend(), Times.Exactly(2));
        Assert.All(h.LogEntries, e => Assert.Equal("manual", e.Context));
        Assert.All(h.LogEntries, e => Assert.Contains("***", e.ToMasked, StringComparison.Ordinal));
        Assert.DoesNotContain(h.LogEntries, e => e.ToMasked is "alice@example.org" or "bob@example.org");
    }

    [Fact]
    public async Task ListUnsubscribe_PresentWithPublicUrl_AbsentWithout()
    {
        var withUrl = new Harness();
        withUrl.Contactable(new Recipient(Alice, "alice@example.org"));
        withUrl.AdminRows(0);
        await withUrl.Service.SendAsync(Request(), CancellationToken.None);

        var headers = Assert.Single(withUrl.Sent).Headers;
        Assert.NotNull(headers);
        Assert.True(headers!.TryGetValue("List-Unsubscribe", out var value));
        Assert.StartsWith("<https://media.example.org/EasyNotif/u/", value, StringComparison.Ordinal);
        Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);

        var token = value.TrimStart('<').TrimEnd('>')["https://media.example.org/EasyNotif/u/".Length..];
        Assert.True(UnsubscribeToken.TryVerify("unsub-secret-0123456789", token, out var uid, out var cat));
        Assert.Equal(Alice, uid);
        Assert.Equal("all", cat);

        var noUrl = new Harness(new PluginConfiguration { UnsubscribeSecret = "x" });
        noUrl.Contactable(new Recipient(Alice, "alice@example.org"));
        noUrl.AdminRows(0);
        await noUrl.Service.SendAsync(Request(), CancellationToken.None);
        Assert.Null(Assert.Single(noUrl.Sent).Headers);
    }

    [Fact]
    public async Task OneFailureDoesNotStopTheRun()
    {
        var h = new Harness(sendSucceeds: false);
        h.Contactable(new Recipient(Alice, "alice@example.org"), new Recipient(Bob, "bob@example.org"));
        h.AdminRows(0);

        var result = await h.Service.SendAsync(Request(), CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, h.Sent.Count);
        h.Quota.Verify(q => q.RecordSend(), Times.Never);
        Assert.All(h.LogEntries, e => Assert.Equal("failed", e.Status));
    }
}
