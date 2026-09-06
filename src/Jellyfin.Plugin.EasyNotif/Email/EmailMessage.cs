namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// A single email to send. Transport-agnostic: the sender turns it into a provider request.
/// </summary>
public sealed class EmailMessage
{
    /// <summary>Gets the single recipient address.</summary>
    public required string To { get; init; }

    /// <summary>Gets the subject line.</summary>
    public required string Subject { get; init; }

    /// <summary>Gets the HTML body, or null for a text-only message.</summary>
    public string? Html { get; init; }

    /// <summary>
    /// Gets the plain-text body. When null and <see cref="Html"/> is set, the sender generates one
    /// from the HTML (required for deliverability).
    /// </summary>
    public string? Text { get; init; }

    /// <summary>Gets an optional Reply-To address for this message.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>Gets optional extra headers, for example <c>List-Unsubscribe</c>.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Gets optional provider tags (key/value), for example the campaign id.</summary>
    public IReadOnlyList<EmailTag>? Tags { get; init; }

    /// <summary>Gets optional file attachments.</summary>
    public IReadOnlyList<EmailAttachment>? Attachments { get; init; }

    /// <summary>
    /// Gets an optional idempotency key. When set, a retry of the same logical send is de-duplicated
    /// by the provider for 24 hours. Left null for messages that may legitimately be re-sent (a
    /// user-triggered test).
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>A provider tag attached to a send, for correlation and reporting.</summary>
/// <param name="Name">The tag name.</param>
/// <param name="Value">The tag value.</param>
public readonly record struct EmailTag(string Name, string Value);

/// <summary>A file attached to an email.</summary>
public sealed class EmailAttachment
{
    /// <summary>Gets the file name shown to the recipient.</summary>
    public required string FileName { get; init; }

    /// <summary>Gets the raw file bytes.</summary>
    public required byte[] Content { get; init; }

    /// <summary>Gets the MIME type, or null to let the provider infer it.</summary>
    public string? ContentType { get; init; }
}

/// <summary>The outcome of a send attempt. Never carries an exception - failures are values.</summary>
/// <param name="Success">True when the provider accepted the message.</param>
/// <param name="ResendId">The provider message id on success, used to correlate webhooks; null otherwise.</param>
/// <param name="StatusCode">The HTTP status from the provider, or 0 when the request never left.</param>
/// <param name="Error">A short human-readable failure reason, or null on success.</param>
public sealed record SendResult(bool Success, string? ResendId, int StatusCode, string? Error)
{
    /// <summary>
    /// Gets a value indicating whether the provider rejected the send only because an identical
    /// idempotency key was already used (HTTP 409). The message was not delivered again, but this is
    /// not a real failure - the original send stands.
    /// </summary>
    public bool Deduplicated { get; init; }
}
