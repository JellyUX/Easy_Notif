namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Sends email through the configured transport. The Resend implementation is the only one in v1;
/// an SMTP implementation can be added without touching callers (Synthese.md section 3.4).
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends one message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome. Never throws for a provider or network failure.</returns>
    Task<SendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a batch of distinct messages in one provider call. Implemented but not on the default
    /// path in v1 (Synthese.md section 3.3); attachments are not supported in a batch.
    /// </summary>
    /// <param name="messages">The messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per input message, in order.</returns>
    Task<IReadOnlyList<SendResult>> SendBatchAsync(IReadOnlyList<EmailMessage> messages, CancellationToken cancellationToken);
}
