using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Sends a one-off admin email to every user with a valid contact address (or a chosen subset, or
/// a single test address). Manual emails are not subject to category preferences
/// (Synthese.md section 7.1). Each send is spaced by the shared <see cref="SendRateLimiter"/>,
/// counted by <see cref="IQuotaGuard"/> and recorded (masked) in <see cref="ISendLog"/>.
/// </summary>
public interface IManualEmailService
{
    /// <summary>Sends the manual email described by <paramref name="request"/>.</summary>
    /// <param name="request">The composed email and its audience.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A per-recipient summary.</returns>
    /// <exception cref="ArgumentException">The request is not valid to send.</exception>
    Task<ManualEmailResult> SendAsync(ManualEmailRequest request, CancellationToken cancellationToken);
}

/// <summary>A composed manual email and its audience.</summary>
/// <param name="Subject">The subject line (required).</param>
/// <param name="Html">The HTML body, or null.</param>
/// <param name="Text">The plain-text body, or null (generated from <paramref name="Html"/> when absent).</param>
/// <param name="Mode">"all", "selected" or "test".</param>
/// <param name="UserIds">The chosen users when <paramref name="Mode"/> is "selected".</param>
/// <param name="TestAddress">The single address when <paramref name="Mode"/> is "test".</param>
/// <param name="Attachments">Optional attachments (total size capped).</param>
public sealed record ManualEmailRequest(
    string Subject,
    string? Html,
    string? Text,
    string Mode,
    IReadOnlyList<Guid>? UserIds,
    string? TestAddress,
    IReadOnlyList<EmailAttachment>? Attachments);

/// <summary>The outcome of a manual send.</summary>
/// <param name="Sent">Number of messages the provider accepted.</param>
/// <param name="Failed">Number the provider rejected.</param>
/// <param name="SkippedNoEmail">Number of intended users with no valid contact address.</param>
/// <param name="Details">One entry per attempted send.</param>
public sealed record ManualEmailResult(int Sent, int Failed, int SkippedNoEmail, IReadOnlyList<ManualSendDetail> Details);

/// <summary>One attempted send in a manual email run.</summary>
/// <param name="MaskedTo">The masked recipient address.</param>
/// <param name="Status">"sent" or "failed".</param>
public sealed record ManualSendDetail(string MaskedTo, string Status);

/// <inheritdoc cref="IManualEmailService"/>
public sealed class ManualEmailService : IManualEmailService
{
    /// <summary>Maximum total size of all attachments on a single manual email.</summary>
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;

    private const string UnsubscribeCategory = "all";

    private readonly IEmailSender _sender;
    private readonly IPreferenceService _preferences;
    private readonly IConfigAccessor _config;
    private readonly IQuotaGuard _quota;
    private readonly ISendLog _sendLog;
    private readonly ILogger<ManualEmailService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ManualEmailService"/> class.</summary>
    /// <param name="sender">The email transport.</param>
    /// <param name="preferences">The preference service (for recipient resolution).</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="quota">The send quota guard.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="logger">Logger.</param>
    public ManualEmailService(
        IEmailSender sender,
        IPreferenceService preferences,
        IConfigAccessor config,
        IQuotaGuard quota,
        ISendLog sendLog,
        ILogger<ManualEmailService> logger)
    {
        _sender = sender;
        _preferences = preferences;
        _config = config;
        _quota = quota;
        _sendLog = sendLog;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ManualEmailResult> SendAsync(ManualEmailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            throw new ArgumentException("A subject is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Html) && string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("An HTML or a text body is required.", nameof(request));
        }

        var attachmentBytes = request.Attachments?.Sum(a => (long)a.Content.Length) ?? 0;
        if (attachmentBytes > MaxAttachmentBytes)
        {
            throw new ArgumentException("The attachments exceed the size limit.", nameof(request));
        }

        var isTest = string.Equals(request.Mode, "test", StringComparison.Ordinal);
        var (recipients, skippedNoEmail) = ResolveRecipients(request);

        var text = string.IsNullOrWhiteSpace(request.Text) ? HtmlToText.Convert(request.Html) : request.Text;
        var cfg = _config.Get();

        var sent = 0;
        var failed = 0;
        var details = new List<ManualSendDetail>(recipients.Count);

        foreach (var recipient in recipients)
        {
            var message = new EmailMessage
            {
                To = recipient.Email,
                Subject = request.Subject,
                Html = string.IsNullOrWhiteSpace(request.Html) ? null : request.Html,
                Text = string.IsNullOrEmpty(text) ? null : text,
                ReplyTo = cfg.ReplyTo,
                Headers = isTest ? null : BuildUnsubscribeHeaders(cfg, recipient.UserId),
                Tags = [new EmailTag("context", "manual")],
                Attachments = request.Attachments,
                IdempotencyKey = null
            };

            var result = await _sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _quota.RecordSend();
                sent++;
            }
            else
            {
                failed++;
            }

            var maskedTo = EmailMasker.Mask(recipient.Email);
            details.Add(new ManualSendDetail(maskedTo, result.Success ? "sent" : "failed"));

            try
            {
                _sendLog.Append(new SendLogEntry
                {
                    Ts = DateTime.UtcNow,
                    Context = isTest ? "manual-test" : "manual",
                    ToMasked = maskedTo,
                    Subject = request.Subject,
                    ResendId = result.ResendId,
                    Status = result.Success ? "sent" : "failed"
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "[EasyNotif] Could not record a manual send in the send log.");
            }
        }

        _logger.LogInformation(
            "[EasyNotif] Manual email \"{Subject}\": {Sent} sent, {Failed} failed, {Skipped} without an address.",
            request.Subject,
            sent,
            failed,
            skippedNoEmail);

        return new ManualEmailResult(sent, failed, skippedNoEmail, details);
    }

    private (IReadOnlyList<Recipient> Recipients, int SkippedNoEmail) ResolveRecipients(ManualEmailRequest request)
    {
        switch (request.Mode)
        {
            case "test":
                if (string.IsNullOrWhiteSpace(request.TestAddress)
                    || !System.Net.Mail.MailAddress.TryCreate(request.TestAddress.Trim(), out _))
                {
                    throw new ArgumentException("A valid test address is required.", nameof(request));
                }

                return ([new Recipient(Guid.Empty, request.TestAddress.Trim())], 0);

            case "selected":
                if (request.UserIds is not { Count: > 0 })
                {
                    throw new ArgumentException("Select at least one user.", nameof(request));
                }

                var chosen = _preferences.GetContactable(request.UserIds);
                return (chosen, request.UserIds.Count - chosen.Count);

            case "all":
                var everyone = _preferences.GetContactable(null);
                var withoutEmail = _preferences.GetAllForAdmin().Count(r => !r.HasEmail);
                return (everyone, withoutEmail);

            default:
                throw new ArgumentException("Unknown recipient mode.", nameof(request));
        }
    }

    private static IReadOnlyDictionary<string, string>? BuildUnsubscribeHeaders(PluginConfiguration cfg, Guid userId)
        => UnsubscribeToken.BuildListUnsubscribeHeaders(cfg.PublicServerUrl, cfg.UnsubscribeSecret, userId, UnsubscribeCategory);
}
