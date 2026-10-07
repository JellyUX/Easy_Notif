using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Sends a one-off admin email to every user with a valid contact address (or a chosen subset, or
/// a single test address). Manual emails are not subject to category preferences
/// (Synthese.md section 7.1), but a send to everyone skips users who opted out of announcements
/// and carries a one-click unsubscribe link; a send to hand-picked users does neither, since the
/// admin chose them explicitly. Each send is spaced by the shared <see cref="SendRateLimiter"/>,
/// counted by <see cref="IQuotaGuard"/> and recorded (masked) in <see cref="ISendLog"/>.
/// </summary>
public interface IManualEmailService
{
    /// <summary>Validates <paramref name="request"/> and resolves its recipients, without sending.</summary>
    /// <param name="request">The composed email and its audience.</param>
    /// <returns>The plan to pass to <see cref="SendAsync(ManualEmailPlan, Guid, IReadOnlySet{Guid}?, Action{ManualSendDetail}?, CancellationToken)"/>.</returns>
    /// <exception cref="ArgumentException">The request is not valid to send.</exception>
    ManualEmailPlan Plan(ManualEmailRequest request);

    /// <summary>Sends the manual email described by <paramref name="request"/>, as a lot of its own.</summary>
    /// <param name="request">The composed email and its audience.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A per-recipient summary.</returns>
    /// <exception cref="ArgumentException">The request is not valid to send.</exception>
    Task<ManualEmailResult> SendAsync(ManualEmailRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a planned manual email as lot <paramref name="lotId"/>. Each recipient's idempotency key
    /// is <c>manual:{lotId}:{userId}</c>, so sending the same lot again never delivers twice.
    /// </summary>
    /// <param name="plan">The plan from <see cref="Plan"/>.</param>
    /// <param name="lotId">The lot id.</param>
    /// <param name="onlyUserIds">When set, only these recipients are sent (a retry of the failures).</param>
    /// <param name="onSent">Called after each attempted send, for progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A per-recipient summary.</returns>
    Task<ManualEmailResult> SendAsync(
        ManualEmailPlan plan,
        Guid lotId,
        IReadOnlySet<Guid>? onlyUserIds,
        Action<ManualSendDetail>? onSent,
        CancellationToken cancellationToken);
}

/// <summary>A validated manual email and its resolved recipients.</summary>
/// <param name="Request">The request.</param>
/// <param name="Recipients">The recipients, in send order.</param>
/// <param name="SkippedNoEmail">Number of intended users with no valid contact address.</param>
public sealed record ManualEmailPlan(ManualEmailRequest Request, IReadOnlyList<Recipient> Recipients, int SkippedNoEmail);

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
/// <param name="Status">"sent", "failed", or "deduplicated" (already sent under the same key).</param>
public sealed record ManualSendDetail(string MaskedTo, string Status)
{
    /// <summary>Gets the recipient's user id (<see cref="Guid.Empty"/> for a test address). Not exposed by the API.</summary>
    public Guid UserId { get; init; }
}

/// <inheritdoc cref="IManualEmailService"/>
public sealed class ManualEmailService : IManualEmailService
{
    /// <summary>Maximum total size of all attachments on a single manual email.</summary>
    public const long MaxAttachmentBytes = 10L * 1024 * 1024;

    private const string UnsubscribeCategory = "all";

    private readonly IEmailSender _sender;
    private readonly IPreferenceService _preferences;
    private readonly IConfigAccessor _config;
    private readonly ISecretStore _secrets;
    private readonly IQuotaGuard _quota;
    private readonly ISendLog _sendLog;
    private readonly ILogger<ManualEmailService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ManualEmailService"/> class.</summary>
    /// <param name="sender">The email transport.</param>
    /// <param name="preferences">The preference service (for recipient resolution).</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="secrets">The transport secret store.</param>
    /// <param name="quota">The send quota guard.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="logger">Logger.</param>
    public ManualEmailService(
        IEmailSender sender,
        IPreferenceService preferences,
        IConfigAccessor config,
        ISecretStore secrets,
        IQuotaGuard quota,
        ISendLog sendLog,
        ILogger<ManualEmailService> logger)
    {
        _sender = sender;
        _preferences = preferences;
        _config = config;
        _secrets = secrets;
        _quota = quota;
        _sendLog = sendLog;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<ManualEmailResult> SendAsync(ManualEmailRequest request, CancellationToken cancellationToken)
        => SendAsync(Plan(request), Guid.NewGuid(), null, null, cancellationToken);

    /// <inheritdoc/>
    public ManualEmailPlan Plan(ManualEmailRequest request)
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

        var (recipients, skippedNoEmail) = ResolveRecipients(request);
        return new ManualEmailPlan(request, recipients, skippedNoEmail);
    }

    /// <inheritdoc/>
    public async Task<ManualEmailResult> SendAsync(
        ManualEmailPlan plan,
        Guid lotId,
        IReadOnlySet<Guid>? onlyUserIds,
        Action<ManualSendDetail>? onSent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;
        var isTest = string.Equals(request.Mode, "test", StringComparison.Ordinal);
        var isAll = string.Equals(request.Mode, "all", StringComparison.Ordinal);
        var recipients = onlyUserIds is null
            ? plan.Recipients
            : [.. plan.Recipients.Where(r => onlyUserIds.Contains(r.UserId))];
        var skippedNoEmail = plan.SkippedNoEmail;

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
                Headers = !isAll ? null : BuildUnsubscribeHeaders(cfg, _secrets.Get().UnsubscribeSecret, recipient.UserId),
                Tags = [new EmailTag("context", "manual")],
                Attachments = request.Attachments,
                IdempotencyKey = ManualKey(lotId, recipient)
            };

            var result = await _sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string status;
            if (result.Success)
            {
                _quota.RecordSend();
                sent++;
                status = "sent";
            }
            else if (result.Deduplicated)
            {
                status = "deduplicated";
            }
            else
            {
                failed++;
                status = "failed";
            }

            var maskedTo = EmailMasker.Mask(recipient.Email);
            var detail = new ManualSendDetail(maskedTo, status) { UserId = recipient.UserId };
            details.Add(detail);
            onSent?.Invoke(detail);

            try
            {
                _sendLog.Append(new SendLogEntry
                {
                    Ts = DateTime.UtcNow,
                    Context = isTest ? "manual-test" : "manual",
                    ToMasked = maskedTo,
                    Subject = request.Subject,
                    ResendId = result.ResendId,
                    Status = status
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
                var everyone = _preferences.GetContactable(null, excludeAnnouncementOptOuts: true);
                var withoutEmail = _preferences.GetAllForAdmin().Count(r => !r.HasEmail);
                return (everyone, withoutEmail);

            default:
                throw new ArgumentException("Unknown recipient mode.", nameof(request));
        }
    }

    // One key per lot and recipient: a replay of the same lot never delivers twice. A test lot has a
    // single address and no user id; the address itself is kept out of the key (and of sent-keys.json).
    private static string ManualKey(Guid lotId, Recipient recipient)
        => recipient.UserId == Guid.Empty
            ? $"manual:{lotId:N}:test"
            : $"manual:{lotId:N}:{recipient.UserId:N}";

    private static IReadOnlyDictionary<string, string>? BuildUnsubscribeHeaders(PluginConfiguration cfg, string? unsubscribeSecret, Guid userId)
        => UnsubscribeToken.BuildListUnsubscribeHeaders(cfg.PublicServerUrl, unsubscribeSecret, userId, UnsubscribeCategory);
}
