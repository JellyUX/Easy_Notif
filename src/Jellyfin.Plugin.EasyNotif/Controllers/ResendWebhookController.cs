using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.EasyNotif.Controllers;

/// <summary>
/// Receives Resend delivery webhooks at <c>POST /EasyNotif/webhooks/resend</c>. Anonymous but
/// signature-gated: an unsigned, mis-signed or stale request gets 401. A verified event updates the
/// matching send-log line by Resend id. The handler always answers 200 once verified, even if the
/// write fails, so Resend does not retry into a loop (R10).
/// </summary>
[ApiController]
[Route("EasyNotif/webhooks")]
public class ResendWebhookController : ControllerBase
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private readonly IConfigAccessor _config;
    private readonly ISendLog _sendLog;
    private readonly ILogger<ResendWebhookController> _logger;
    private readonly IEasyNotifLog _easyNotifLog;

    /// <summary>Initializes a new instance of the <see cref="ResendWebhookController"/> class.</summary>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    public ResendWebhookController(IConfigAccessor config, ISendLog sendLog, ILogger<ResendWebhookController> logger, IEasyNotifLog easyNotifLog)
    {
        _config = config;
        _sendLog = sendLog;
        _logger = logger;
        _easyNotifLog = easyNotifLog;
    }

    /// <summary>Handles one Resend webhook delivery.</summary>
    /// <returns>200 when accepted; 400 when the signature headers are missing; 401 when verification fails.</returns>
    [HttpPost("resend")]
    [AllowAnonymous]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Resend()
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var id = Header("svix-id", "webhook-id");
        var timestamp = Header("svix-timestamp", "webhook-timestamp");
        var signature = Header("svix-signature", "webhook-signature");
        if (id is null || timestamp is null || signature is null)
        {
            return BadRequest();
        }

        var secret = _config.Get().WebhookSigningSecret;
        if (!SvixVerifier.Verify(secret, id, timestamp, body, signature, DateTimeOffset.UtcNow, Tolerance))
        {
            _logger.LogWarning("[EasyNotif] Rejected a Resend webhook: signature verification failed.");
            return Unauthorized();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var emailId = root.TryGetProperty("data", out var data) && data.TryGetProperty("email_id", out var idElement)
                ? idElement.GetString()
                : null;

            if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(emailId))
            {
                var matched = _sendLog.UpdateStatus(emailId, type);
                var fields = new Dictionary<string, object?> { ["type"] = type, ["resendId"] = emailId, ["matched"] = matched };
                if (type is "email.bounced" or "email.complained")
                {
                    _logger.LogWarning("[EasyNotif] Resend reported {Type} for a delivered message.", type);
                    _easyNotifLog.Warn("webhook.received", fields);
                }
                else
                {
                    _logger.LogInformation("[EasyNotif] Recorded Resend webhook {Type} (matched an existing send: {Matched}).", type, matched);
                    _easyNotifLog.Info("webhook.received", fields);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] Failed to record a verified Resend webhook.");
        }

        return Ok();
    }

    private string? Header(string primary, string fallback)
    {
        if (Request.Headers.TryGetValue(primary, out var value) && !StringValues.IsNullOrEmpty(value))
        {
            return value.ToString();
        }

        return Request.Headers.TryGetValue(fallback, out var alt) && !StringValues.IsNullOrEmpty(alt)
            ? alt.ToString()
            : null;
    }
}
