using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Util;
using Jellyfin.Plugin.EasyNotif.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.EasyNotif.Controllers;

/// <summary>
/// Receives Resend delivery webhooks at <c>POST /EasyNotif/webhooks/resend</c>. Anonymous but
/// signature-gated: an unsigned, mis-signed or stale request gets 401, a body over 64 KiB gets 413
/// before any verification. A verified event updates the matching send-log line by Resend id, once
/// per <c>svix-id</c> (a replay within 10 minutes is ignored). A complaint turns every email off for
/// the recipient and a hard bounce or suppression marks their address rejected (PRIV-05). The
/// handler always answers 200 once
/// verified, even if the write fails, so Resend does not retry into a loop (R10).
/// </summary>
[ApiController]
[Route("EasyNotif/webhooks")]
public class ResendWebhookController : ControllerBase
{
    /// <summary>The largest accepted webhook body; Resend events are a few KiB.</summary>
    internal const int MaxBodyBytes = 64 * 1024;

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private readonly ISecretStore _secrets;
    private readonly ISendLog _sendLog;
    private readonly WebhookReplayGuard _replayGuard;
    private readonly IPreferenceService _preferences;
    private readonly ILogger<ResendWebhookController> _logger;
    private readonly IEasyNotifLog _easyNotifLog;

    /// <summary>Initializes a new instance of the <see cref="ResendWebhookController"/> class.</summary>
    /// <param name="secrets">The transport secret store.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="replayGuard">The seen-<c>svix-id</c> cache.</param>
    /// <param name="preferences">The user preferences, changed by a complaint or a hard bounce.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    public ResendWebhookController(ISecretStore secrets, ISendLog sendLog, WebhookReplayGuard replayGuard, IPreferenceService preferences, ILogger<ResendWebhookController> logger, IEasyNotifLog easyNotifLog)
    {
        _secrets = secrets;
        _sendLog = sendLog;
        _replayGuard = replayGuard;
        _preferences = preferences;
        _logger = logger;
        _easyNotifLog = easyNotifLog;
    }

    /// <summary>Handles one Resend webhook delivery.</summary>
    /// <returns>200 when accepted or replayed; 400 when the signature headers are missing; 401 when verification fails; 413 when the body is too large.</returns>
    [HttpPost("resend")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxBodyBytes)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Resend()
    {
        // The attribute bounds Kestrel; this bounded read also covers hosts that ignore it.
        if (Request.ContentLength > MaxBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadBodyAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (body is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var id = Header("svix-id", "webhook-id");
        var timestamp = Header("svix-timestamp", "webhook-timestamp");
        var signature = Header("svix-signature", "webhook-signature");
        if (id is null || timestamp is null || signature is null)
        {
            return BadRequest();
        }

        var secret = _secrets.Get().WebhookSigningSecret;
        if (!SvixVerifier.Verify(secret, id, timestamp, body, signature, DateTimeOffset.UtcNow, Tolerance))
        {
            _logger.LogWarning("[EasyNotif] Rejected a Resend webhook: signature verification failed.");
            return Unauthorized();
        }

        if (!_replayGuard.TryRecord(id))
        {
            _logger.LogInformation("[EasyNotif] Ignored a replayed Resend webhook.");
            return Ok();
        }

        string? type;
        string? emailId;
        bool permanentBounce;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            var hasData = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
            emailId = hasData && data.TryGetProperty("email_id", out var idElement) ? idElement.GetString() : null;
            permanentBounce = hasData
                && data.TryGetProperty("bounce", out var bounce)
                && bounce.ValueKind == JsonValueKind.Object
                && bounce.TryGetProperty("type", out var bounceType)
                && string.Equals(bounceType.GetString(), "Permanent", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] Failed to read a verified Resend webhook.");
            return Ok();
        }

        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(emailId))
        {
            return Ok();
        }

        try
        {
            var matched = _sendLog.UpdateStatus(emailId, type);
            var fields = new Dictionary<string, object?> { ["type"] = type, ["resendId"] = emailId, ["matched"] = matched };
            if (type is "email.bounced" or "email.complained" or "email.suppressed")
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
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] Failed to record a verified Resend webhook.");
        }

        // Acted on separately: a failed status write must not cancel an opt-out (PRIV-05).
        var complaint = type == "email.complained";
        var rejected = (type == "email.bounced" && permanentBounce) || type == "email.suppressed";
        if (complaint || rejected)
        {
            ApplyToRecipient(emailId, complaint);
        }

        return Ok();
    }

    // A complaint turns every email off (DEC-51); a hard bounce or suppression marks the address
    // rejected (DEC-49). The user is found through the send log (DEC-48).
    private void ApplyToRecipient(string emailId, bool complaint)
    {
        try
        {
            var userIds = _sendLog.UserIdsFor(emailId);
            if (userIds.Count == 0)
            {
                _logger.LogWarning("[EasyNotif] No user recorded for a {Kind} webhook; nothing changed.", complaint ? "complaint" : "rejected-address");
                return;
            }

            foreach (var userId in userIds)
            {
                var changed = complaint ? _preferences.ApplyComplaint(userId) : _preferences.MarkEmailRejected(userId);
                var fields = new Dictionary<string, object?> { ["userId"] = userId, ["resendId"] = emailId, ["changed"] = changed };
                if (complaint)
                {
                    _logger.LogWarning("[EasyNotif] Spam complaint: turned every email off for user {UserId}.", EmailMasker.MaskUserId(userId));
                    _easyNotifLog.Warn("webhook.complaint", fields);
                }
                else
                {
                    _logger.LogWarning("[EasyNotif] Address rejected by Resend: stopped mailing user {UserId}.", EmailMasker.MaskUserId(userId));
                    _easyNotifLog.Warn("webhook.rejected", fields);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] Failed to apply a verified Resend {Kind} webhook to the preferences.", complaint ? "complaint" : "rejected-address");
        }
    }

    private async Task<string?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        // One byte past the limit is enough to tell an oversized body apart.
        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length
            && (read = await Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        return length > MaxBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, length);
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
