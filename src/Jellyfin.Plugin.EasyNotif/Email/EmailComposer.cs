using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <inheritdoc cref="IEmailComposer"/>
public sealed class EmailComposer : IEmailComposer
{
    private readonly INewsletterDigestService _digest;
    private readonly NewsletterComposer _newsletter;
    private readonly ServerLinkContext _links;
    private readonly IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="EmailComposer"/> class.</summary>
    /// <param name="digest">The new-media digest service.</param>
    /// <param name="newsletter">The newsletter composer.</param>
    /// <param name="links">The captured server identity (for the heading).</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public EmailComposer(
        INewsletterDigestService digest,
        NewsletterComposer newsletter,
        ServerLinkContext links,
        IEasyNotifLog log)
    {
        _digest = digest;
        _newsletter = newsletter;
        _links = links;
        _log = log;
    }

    /// <inheritdoc/>
    public Task<PreparedCampaign> PrepareAsync(Campaign campaign, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        if (campaign.Type == CampaignType.Newsletter)
        {
            var since = campaign.LastSentUtc ?? nowUtc.AddDays(-7);
            var digest = _digest.GetNewSince(since);

            _log.Info("newsletter.digest", new Dictionary<string, object?>
            {
                ["campaignId"] = campaign.Id,
                ["since"] = since,
                ["movies"] = digest.Movies.Count,
                ["series"] = digest.Series.Count,
                ["totalItems"] = digest.TotalItems
            });

            return Task.FromResult(new PreparedCampaign
            {
                ShouldSend = !digest.IsEmpty,
                SkipReason = digest.IsEmpty ? "empty-digest" : null,
                Render = _ => _newsletter.Compose(campaign, digest, _links.ServerName),
                MovieCount = digest.Movies.Count,
                SeriesCount = digest.Series.Count
            });
        }

        // WeeklyRecap and any future type: the Phase 7 placeholder until its own phase lands.
        return Task.FromResult(new PreparedCampaign
        {
            ShouldSend = true,
            Render = _ => new EmailContent(
                $"[Easy Notif] {campaign.Type} ({campaign.MailLanguage})",
                "<p>Placeholder content, replaced in a later phase.</p>",
                "Placeholder content, replaced in a later phase.")
        });
    }
}
