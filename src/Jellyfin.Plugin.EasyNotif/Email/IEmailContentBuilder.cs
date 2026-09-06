using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>The rendered subject and body of a campaign email for one recipient.</summary>
/// <param name="Subject">The subject line.</param>
/// <param name="Html">The HTML body, or null.</param>
/// <param name="Text">The plain-text body, or null (the sender derives one from the HTML when absent).</param>
public sealed record EmailContent(string Subject, string? Html, string? Text);

/// <summary>
/// Renders the content of a campaign email. Phase 7 ships <see cref="StubEmailContentBuilder"/>;
/// Phases 8 and 9 replace it with a real composer routed by <see cref="Campaign.Type"/>
/// (Synthese.md section 9.1).
/// </summary>
public interface IEmailContentBuilder
{
    /// <summary>Builds the email content for a campaign and a recipient.</summary>
    /// <param name="campaign">The campaign being dispatched.</param>
    /// <param name="recipient">The recipient the content is personalised for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered content.</returns>
    Task<EmailContent> BuildAsync(Campaign campaign, Recipient recipient, CancellationToken cancellationToken);
}

/// <summary>
/// Placeholder content builder used until the real templates land (Phases 8-9). Produces a minimal,
/// clearly-marked message so the scheduling engine can be exercised end to end.
/// </summary>
public sealed class StubEmailContentBuilder : IEmailContentBuilder
{
    /// <inheritdoc/>
    public Task<EmailContent> BuildAsync(Campaign campaign, Recipient recipient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        return Task.FromResult(new EmailContent(
            $"[Easy Notif] {campaign.Type} ({campaign.MailLanguage})",
            "<p>Placeholder content, replaced in a later phase.</p>",
            "Placeholder content, replaced in a later phase."));
    }
}
