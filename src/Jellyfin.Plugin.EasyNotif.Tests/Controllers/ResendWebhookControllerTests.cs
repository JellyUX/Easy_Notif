using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Controllers;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Controllers;

/// <summary>
/// Covers <see cref="ResendWebhookController"/>: 400 without signature headers, 401 when the secret
/// is unset or the signature is wrong (and no write happens), 200 plus a status update on a valid
/// signature, and 200 even when the write throws.
/// </summary>
public sealed class ResendWebhookControllerTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private static string Sign(string id, string timestamp, string body)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(Secret["whsec_".Length..]));
        return "v1," + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
    }

    private static ResendWebhookController Build(Mock<ISendLog> sendLog, string? secret = Secret)
    {
        var config = new FakeConfigAccessor(new PluginConfiguration { WebhookSigningSecret = secret });
        return new ResendWebhookController(config, sendLog.Object, NullLogger<ResendWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static void SetRequest(ResendWebhookController controller, string body, string? id, string? timestamp, string? signature)
    {
        var request = controller.ControllerContext.HttpContext.Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        request.ContentType = "application/json";
        if (id is not null)
        {
            request.Headers["svix-id"] = id;
        }

        if (timestamp is not null)
        {
            request.Headers["svix-timestamp"] = timestamp;
        }

        if (signature is not null)
        {
            request.Headers["svix-signature"] = signature;
        }
    }

    private static string NowTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Resend_WithoutSignatureHeaders_Returns400()
    {
        var sendLog = new Mock<ISendLog>();
        var controller = Build(sendLog);
        SetRequest(controller, "{}", id: null, timestamp: null, signature: null);

        var result = await controller.Resend();

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Resend_WhenSecretIsNotConfigured_Returns401()
    {
        var sendLog = new Mock<ISendLog>();
        var controller = Build(sendLog, secret: null);
        var ts = NowTimestamp();
        SetRequest(controller, "{}", "msg_1", ts, Sign("msg_1", ts, "{}"));

        var result = await controller.Resend();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Resend_WithInvalidSignature_Returns401_AndDoesNotWrite()
    {
        var sendLog = new Mock<ISendLog>();
        var controller = Build(sendLog);
        var ts = NowTimestamp();
        SetRequest(controller, "{\"type\":\"email.delivered\"}", "msg_1", ts, "v1,wrong");

        var result = await controller.Resend();

        Assert.IsType<UnauthorizedResult>(result);
        sendLog.Verify(l => l.UpdateStatus(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Resend_WithValidSignature_Returns200_AndUpdatesTheStatus()
    {
        var sendLog = new Mock<ISendLog>();
        var controller = Build(sendLog);
        var ts = NowTimestamp();
        const string Body = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"re_abc\"}}";
        SetRequest(controller, Body, "msg_1", ts, Sign("msg_1", ts, Body));

        var result = await controller.Resend();

        Assert.IsType<OkResult>(result);
        sendLog.Verify(l => l.UpdateStatus("re_abc", "email.delivered"), Times.Once);
    }

    [Fact]
    public async Task Resend_ForABounce_Returns200()
    {
        var sendLog = new Mock<ISendLog>();
        var controller = Build(sendLog);
        var ts = NowTimestamp();
        const string Body = "{\"type\":\"email.bounced\",\"data\":{\"email_id\":\"re_abc\"}}";
        SetRequest(controller, Body, "msg_1", ts, Sign("msg_1", ts, Body));

        var result = await controller.Resend();

        Assert.IsType<OkResult>(result);
        sendLog.Verify(l => l.UpdateStatus("re_abc", "email.bounced"), Times.Once);
    }

    [Fact]
    public async Task Resend_WhenTheWriteThrows_StillReturns200()
    {
        var sendLog = new Mock<ISendLog>();
        sendLog.Setup(l => l.UpdateStatus(It.IsAny<string>(), It.IsAny<string>())).Throws(new IOException("disk full"));
        var controller = Build(sendLog);
        var ts = NowTimestamp();
        const string Body = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"re_abc\"}}";
        SetRequest(controller, Body, "msg_1", ts, Sign("msg_1", ts, Body));

        var result = await controller.Resend();

        Assert.IsType<OkResult>(result);
    }
}
