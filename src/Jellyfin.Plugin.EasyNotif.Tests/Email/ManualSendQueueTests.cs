using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="ManualSendQueue"/> over a real <see cref="ManualEmailService"/>: a lot runs in
/// the background and reports progress, an invalid request throws before any lot exists, a retry
/// sends only the failed recipients with their original keys, and a finished lot expires after the
/// retention.
/// </summary>
public sealed class ManualSendQueueTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carol = Guid.NewGuid();

    private readonly List<EmailMessage> _sent = [];
    private readonly Mock<IEmailSender> _sender = new();
    private readonly Mock<IPreferenceService> _preferences = new();
    private DateTime _now = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    public ManualSendQueueTests()
    {
        _preferences.Setup(p => p.GetContactable(It.IsAny<IReadOnlyCollection<Guid>?>(), It.IsAny<bool>()))
            .Returns([new Recipient(Alice, "alice@example.org"), new Recipient(Bob, "bob@example.org"), new Recipient(Carol, "carol@example.org")]);
        _preferences.Setup(p => p.GetAllForAdmin()).Returns([]);
        Respond(_ => new SendResult(true, "re", 200, null));
    }

    private void Respond(Func<EmailMessage, SendResult> respond)
        => _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>((m, _) =>
            {
                lock (_sent)
                {
                    _sent.Add(m);
                }

                return Task.FromResult(respond(m));
            });

    private ManualSendQueue Build()
    {
        var manual = new ManualEmailService(
            _sender.Object,
            _preferences.Object,
            new FakeConfigAccessor(new PluginConfiguration()),
            new FakeSecretStore(),
            new Mock<IQuotaGuard>().Object,
            new Mock<ISendLog>().Object,
            NullLogger<ManualEmailService>.Instance);
        return new ManualSendQueue(manual, NullLogger<ManualSendQueue>.Instance, () => _now);
    }

    private static ManualEmailRequest Request(string mode = "all") => new("Announcement", null, "Hello", mode, null, null, null);

    [Fact]
    public async Task Start_RunsTheLotInTheBackground_AndReportsEveryRecipient()
    {
        var gate = new TaskCompletionSource();
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns<EmailMessage, CancellationToken>(async (m, _) =>
            {
                await gate.Task.ConfigureAwait(false);
                lock (_sent)
                {
                    _sent.Add(m);
                }

                return new SendResult(true, "re", 200, null);
            });
        using var queue = Build();

        var started = queue.Start(Request());
        Assert.Equal("running", started.Status);
        Assert.Equal(3, started.Total);
        Assert.Empty(started.Details);

        gate.SetResult();
        await queue.Completion(started.Id);

        var done = queue.Get(started.Id)!;
        Assert.Equal("done", done.Status);
        Assert.Equal(3, done.Sent);
        Assert.Equal(3, done.Details.Count);
        Assert.Equal(started.Id, queue.Latest()!.Id);
        Assert.All(_sent, m => Assert.StartsWith($"manual:{started.Id:N}:", m.IdempotencyKey));
    }

    [Fact]
    public void Start_InvalidRequest_ThrowsAndHoldsNoLot()
    {
        using var queue = Build();

        Assert.Throws<ArgumentException>(() => queue.Start(Request(mode: "nope")));
        Assert.Null(queue.Latest());
    }

    [Fact]
    public async Task Retry_SendsOnlyTheFailedRecipients_WithTheirOriginalKeys()
    {
        Respond(m => m.To == "bob@example.org" ? new SendResult(false, null, 503, "down") : new SendResult(true, "re", 200, null));
        using var queue = Build();
        var lot = queue.Start(Request());
        await queue.Completion(lot.Id);
        Assert.Equal(1, queue.Get(lot.Id)!.Failed);
        var bobKey = _sent.Single(m => m.To == "bob@example.org").IdempotencyKey;

        Respond(_ => new SendResult(true, "re", 200, null));
        var (outcome, _) = queue.Retry(lot.Id);
        await queue.Completion(lot.Id);

        Assert.Equal(ManualRetryOutcome.Started, outcome);
        var retried = Assert.Single(_sent.Skip(3));
        Assert.Equal(bobKey, retried.IdempotencyKey);
        var after = queue.Get(lot.Id)!;
        Assert.Equal(3, after.Sent);
        Assert.Equal(0, after.Failed);
        Assert.Equal(3, after.Details.Count);
        Assert.Equal(ManualRetryOutcome.NothingToRetry, queue.Retry(lot.Id).Outcome);
    }

    [Fact]
    public async Task FinishedLot_ExpiresAfterTheRetention()
    {
        using var queue = Build();
        var lot = queue.Start(Request());
        await queue.Completion(lot.Id);

        _now += ManualSendQueue.Retention + TimeSpan.FromMinutes(1);

        Assert.Null(queue.Get(lot.Id));
        Assert.Equal(ManualRetryOutcome.NotFound, queue.Retry(lot.Id).Outcome);
    }
}
