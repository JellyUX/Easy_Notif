using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="ResendEmailSender"/> against a stub HTTP handler: success parsing, the
/// configuration guard (no request when unconfigured), the single retry on 429/5xx, no retry on a
/// client error, the Idempotency-Key header, generated text, network-failure handling, and R15
/// (the API key never reaches a log line, the recipient is masked).
/// </summary>
public class ResendEmailSenderTests
{
    private const string ApiKey = "re_test_0123456789ABCDEF";

    private static PluginConfiguration Config() => new()
    {
        ResendApiKey = ApiKey,
        FromEmail = "sender@example.com",
        FromName = "Lili",
        ReplyTo = "reply@example.com"
    };

    private static (ResendEmailSender Sender, StubHttpMessageHandler Http, CapturingLogger<ResendEmailSender> Logger, FakeEasyNotifLog EasyNotifLog) Build(
        PluginConfiguration? config = null)
    {
        var http = new StubHttpMessageHandler();
        var logger = new CapturingLogger<ResendEmailSender>();
        var easyNotifLog = new FakeEasyNotifLog();
        var limiter = new SendRateLimiter(TimeSpan.Zero, () => DateTimeOffset.UtcNow, (_, _) => Task.CompletedTask);
        var sender = new ResendEmailSender(
            http,
            new FakeConfigAccessor(config ?? Config()),
            limiter,
            logger,
            easyNotifLog,
            (_, _) => Task.CompletedTask);
        return (sender, http, logger, easyNotifLog);
    }

    private static EmailMessage Message(string? idempotencyKey = null) => new()
    {
        To = "alice.recipient@example.org",
        Subject = "Test subject",
        Html = "<p>Hello</p>",
        IdempotencyKey = idempotencyKey
    };

    [Fact]
    public async Task SendAsync_OnSuccess_ReturnsTheResendId()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"abc-123\"}");

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("abc-123", result.ResendId);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_BuildsTheExpectedRequestBody()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"x\"}");

        await sender.SendAsync(Message(), CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.resend.com/emails", request.Uri!.ToString());
        Assert.Equal("Bearer " + ApiKey, request.Authorization);

        using var doc = JsonDocument.Parse(request.Body!);
        var root = doc.RootElement;
        Assert.Equal("Lili <sender@example.com>", root.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("to").ValueKind);
        Assert.Equal("alice.recipient@example.org", root.GetProperty("to")[0].GetString());
        Assert.Equal("reply@example.com", root.GetProperty("reply_to").GetString());
    }

    [Fact]
    public async Task SendAsync_On422_ReturnsFailureWithoutThrowing()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.UnprocessableEntity, "{\"message\":\"Invalid from address\",\"name\":\"validation_error\"}");

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(422, result.StatusCode);
        Assert.Equal("Invalid from address", result.Error);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task SendAsync_On409_IsDeduplicatedNotFailed_AndLogsSoftly()
    {
        var (sender, http, _, easyNotifLog) = Build();
        http.EnqueueJson(HttpStatusCode.Conflict, "{\"message\":\"This idempotency key has been used\"}");

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Deduplicated);
        Assert.Equal(409, result.StatusCode);
        Assert.Single(http.Requests);
        Assert.Contains(easyNotifLog.Entries, e => e.EventType == "email.deduplicated" && e.Level == "Info");
        Assert.DoesNotContain(easyNotifLog.Entries, e => e.EventType == "email.failed");
    }

    [Fact]
    public async Task SendAsync_On429_RetriesOnceThenSucceeds()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.TooManyRequests, "{\"message\":\"rate limited\"}", retryAfterSeconds: 1);
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"after-retry\"}");

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("after-retry", result.ResendId);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_On500_RetriesOnce()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.InternalServerError, "{}");
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"ok\"}");

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task SendAsync_On401_DoesNotRetry()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.Unauthorized, "{\"message\":\"bad key\"}");

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task SendAsync_WithIdempotencyKey_SetsTheHeader_OtherwiseOmitsIt()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"1\"}");
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"2\"}");

        await sender.SendAsync(Message(idempotencyKey: "campaign:20260101:user"), CancellationToken.None);
        await sender.SendAsync(Message(idempotencyKey: null), CancellationToken.None);

        Assert.Equal("campaign:20260101:user", http.Requests[0].IdempotencyKey);
        Assert.Null(http.Requests[1].IdempotencyKey);
    }

    [Fact]
    public async Task SendAsync_WithHtmlOnly_GeneratesANonEmptyPlainTextPart()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"x\"}");

        await sender.SendAsync(
            new EmailMessage { To = "a@b.co", Subject = "s", Html = "<p>Visible line</p>" },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(http.Requests[0].Body!);
        var text = doc.RootElement.GetProperty("text").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("<", text!, StringComparison.Ordinal);
        Assert.Contains("Visible line", text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_WhenNotConfigured_MakesNoHttpRequest()
    {
        var (sender, http, _, _) = Build(new PluginConfiguration { ResendApiKey = null, FromEmail = "sender@example.com" });

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task SendAsync_OnNetworkFailure_ReturnsFailureWithoutThrowing()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueNetworkFailure();

        var result = await sender.SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, result.StatusCode);
    }

    [Fact]
    public async Task SendAsync_NeverLogsTheApiKey_AndMasksTheRecipient()
    {
        var (sender, http, logger, _) = Build();
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"abc\"}");

        await sender.SendAsync(Message(), CancellationToken.None);

        Assert.DoesNotContain(ApiKey, logger.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("alice.recipient@example.org", logger.AllText, StringComparison.Ordinal);
        Assert.Contains("a***t@example.org", logger.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_OnFailure_NeverLogsTheApiKey()
    {
        var (sender, http, logger, _) = Build();
        http.EnqueueJson(HttpStatusCode.Unauthorized, "{\"message\":\"nope\"}");

        await sender.SendAsync(Message(), CancellationToken.None);

        Assert.DoesNotContain(ApiKey, logger.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendBatchAsync_PostsOnceToTheBatchEndpoint()
    {
        var (sender, http, _, _) = Build();
        http.EnqueueJson(HttpStatusCode.OK, "{\"data\":[{\"id\":\"a\"},{\"id\":\"b\"}]}");

        var results = await sender.SendBatchAsync(
            [
                new EmailMessage { To = "a@b.co", Subject = "s1", Text = "t1" },
                new EmailMessage { To = "c@d.co", Subject = "s2", Text = "t2" }
            ],
            CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.resend.com/emails/batch", request.Uri!.ToString());
        using var doc = JsonDocument.Parse(request.Body!);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Collection(
            results,
            r => Assert.Equal("a", r.ResendId),
            r => Assert.Equal("b", r.ResendId));
    }

    [Fact]
    public async Task SendAsync_OnSuccess_LogsAnEmailSentEvent_WithDuration()
    {
        var (sender, http, _, easyNotifLog) = Build();
        http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"abc-123\"}");

        await sender.SendAsync(Message(), CancellationToken.None);

        var entry = Assert.Single(easyNotifLog.Entries);
        Assert.Equal("Info", entry.Level);
        Assert.Equal("email.sent", entry.EventType);
        Assert.Equal("abc-123", entry.Fields!["resendId"]);
        Assert.True((long)entry.Fields["durationMs"]! >= 0);
    }

    [Fact]
    public async Task SendAsync_WhenNotConfigured_LogsAnEmailFailedEvent_WithNoHttpRequest()
    {
        var (sender, http, _, easyNotifLog) = Build(new PluginConfiguration { ResendApiKey = null, FromEmail = "sender@example.com" });

        await sender.SendAsync(Message(), CancellationToken.None);

        Assert.Empty(http.Requests);
        var entry = Assert.Single(easyNotifLog.Entries);
        Assert.Equal("Error", entry.Level);
        Assert.Equal("email.failed", entry.EventType);
    }

    [Fact]
    public async Task SendAsync_OnFailure_LogsAnEmailFailedEvent()
    {
        var (sender, http, _, easyNotifLog) = Build();
        http.EnqueueJson(HttpStatusCode.Unauthorized, "{\"message\":\"bad key\"}");

        await sender.SendAsync(Message(), CancellationToken.None);

        var entry = Assert.Single(easyNotifLog.Entries);
        Assert.Equal("Error", entry.Level);
        Assert.Equal("email.failed", entry.EventType);
        Assert.Equal(401, entry.Fields!["httpStatus"]);
    }

    [Fact]
    public async Task SendAsync_ThroughARealEasyNotifLog_MasksTheRecipientOnDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "enotif-sender-log-tests-" + Guid.NewGuid());
        var paths = new Moq.Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(tempDir);
        var configManager = new Moq.Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(new MediaBrowser.Model.Configuration.ServerConfiguration());
        using var realLog = new Jellyfin.Plugin.EasyNotif.Logging.EasyNotifLog(
            paths.Object,
            configManager.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.EasyNotif.Logging.EasyNotifLog>.Instance);

        try
        {
            var http = new StubHttpMessageHandler();
            http.EnqueueJson(HttpStatusCode.OK, "{\"id\":\"1\"}");
            var limiter = new SendRateLimiter(TimeSpan.Zero, () => DateTimeOffset.UtcNow, (_, _) => Task.CompletedTask);
            var sender = new ResendEmailSender(
                http,
                new FakeConfigAccessor(Config()),
                limiter,
                new CapturingLogger<ResendEmailSender>(),
                realLog,
                (_, _) => Task.CompletedTask);

            await sender.SendAsync(Message(), CancellationToken.None);
            realLog.Dispose();

            var logFile = Directory.GetFiles(Path.Combine(tempDir, "Jellyfin.Plugin.EasyNotif", "logs"), "easynotif-*.log").Single();
            var text = File.ReadAllText(logFile);
            Assert.DoesNotContain("alice.recipient@example.org", text, StringComparison.Ordinal);
            Assert.Contains("a***t@example.org", text, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
