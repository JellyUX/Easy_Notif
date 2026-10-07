using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Recap;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Util;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <inheritdoc cref="IEmailComposer"/>
public sealed class EmailComposer : IEmailComposer
{
    /// <summary>The longest newsletter window, whatever the last send.</summary>
    public static readonly TimeSpan MaxNewsletterWindow = TimeSpan.FromDays(14);

    private readonly INewsletterDigestService _digest;
    private readonly IAccessFilter _access;
    private readonly NewsletterComposer _newsletter;
    private readonly IWeeklyRecapService _recap;
    private readonly WeeklyRecapComposer _recapComposer;
    private readonly ServerLinkContext _links;
    private readonly IConfigAccessor _config;
    private readonly ISecretStore _secrets;
    private readonly IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="EmailComposer"/> class.</summary>
    /// <param name="digest">The new-media digest service.</param>
    /// <param name="access">The per-user access filter applied to the newsletter.</param>
    /// <param name="newsletter">The newsletter composer.</param>
    /// <param name="recap">The per-user weekly recap service.</param>
    /// <param name="recapComposer">The weekly recap composer.</param>
    /// <param name="links">The captured server identity (for the heading).</param>
    /// <param name="config">The plugin configuration accessor (for the unsubscribe link).</param>
    /// <param name="secrets">The transport secret store (for the unsubscribe link).</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public EmailComposer(
        INewsletterDigestService digest,
        IAccessFilter access,
        NewsletterComposer newsletter,
        IWeeklyRecapService recap,
        WeeklyRecapComposer recapComposer,
        ServerLinkContext links,
        IConfigAccessor config,
        ISecretStore secrets,
        IEasyNotifLog log)
    {
        _digest = digest;
        _access = access;
        _newsletter = newsletter;
        _recap = recap;
        _recapComposer = recapComposer;
        _links = links;
        _config = config;
        _secrets = secrets;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<PreparedCampaign> PrepareAsync(Campaign campaign, DateTime nowUtc, CancellationToken cancellationToken, bool freshWindow = false)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        var templateId = string.IsNullOrEmpty(campaign.TemplateId)
            ? TemplateStore.DefaultTemplateId(campaign.Type)
            : campaign.TemplateId;

        var cfg = _config.Get();
        var unsubscribeSecret = _secrets.Get().UnsubscribeSecret;
        var category = campaign.Category.ToString().ToLowerInvariant();
        var lang = campaign.MailLanguage == "en" ? "en" : "fr";

        string? UnsubscribeUrl(Recipient recipient)
        {
            if (recipient.UserId == Guid.Empty
                || !PublicUrl.IsValid(cfg.PublicServerUrl)
                || string.IsNullOrWhiteSpace(unsubscribeSecret))
            {
                return null;
            }

            var token = UnsubscribeToken.Create(unsubscribeSecret, recipient.UserId, category);
            return $"{cfg.PublicServerUrl.TrimEnd('/')}/EasyNotif/u/{token}?lang={lang}";
        }

        if (campaign.Type == CampaignType.Newsletter)
        {
            // A campaign re-enabled after months would otherwise list everything since its last send.
            var since = freshWindow ? nowUtc.AddDays(-7) : campaign.LastSentUtc ?? nowUtc.AddDays(-7);
            if (since < nowUtc - MaxNewsletterWindow)
            {
                since = nowUtc - MaxNewsletterWindow;
            }

            var digest = await _digest.GetNewSinceAsync(since, cancellationToken).ConfigureAwait(false);
            var maxMovies = Math.Clamp(cfg.NewsletterMaxMovies, 1, PluginConfiguration.MaxNewsletterCap);
            var maxSeries = Math.Clamp(cfg.NewsletterMaxSeries, 1, PluginConfiguration.MaxNewsletterCap);
            var libraryUrl = PublicUrl.IsValid(cfg.PublicServerUrl) ? $"{cfg.PublicServerUrl.TrimEnd('/')}/web/" : null;

            _log.Info("newsletter.digest", new Dictionary<string, object?>
            {
                ["campaignId"] = campaign.Id,
                ["since"] = since,
                ["movies"] = digest.Movies.Count,
                ["series"] = digest.Series.Count,
                ["totalItems"] = digest.TotalItems,
                ["inlinePosters"] = digest.Posters.Count
            });

            return new PreparedCampaign
            {
                ShouldSend = !digest.IsEmpty,
                SkipReason = digest.IsEmpty ? "empty-digest" : null,
                Render = recipient =>
                {
                    // The admin preview (no account) sees the whole digest; an empty digest has
                    // nothing to leak. Anyone else gets only what Jellyfin lets them see.
                    var scoped = recipient.UserId == Guid.Empty || digest.IsEmpty
                        ? digest
                        : digest.For(_access.VisibleTo(recipient.UserId, digest.ItemIds));
                    return scoped.IsEmpty && !digest.IsEmpty
                        ? null
                        : _newsletter.Compose(campaign, scoped.Capped(maxMovies, maxSeries), _links.ServerName, templateId, UnsubscribeUrl(recipient), libraryUrl);
                },
                MovieCount = digest.Movies.Count,
                SeriesCount = digest.Series.Count,
                MarkAnnounced = digest.IsEmpty ? null : runUtc => _digest.MarkAnnounced(digest.Keys, runUtc)
            };
        }

        if (campaign.Type == CampaignType.WeeklyRecap)
        {
            // Always sent, even for a quiet week; the body is built per recipient.
            return new PreparedCampaign
            {
                ShouldSend = true,
                Render = recipient => _recapComposer.Compose(
                    campaign,
                    _recap.BuildFor(recipient.UserId, nowUtc),
                    _links.ServerName,
                    templateId,
                    UnsubscribeUrl(recipient))
            };
        }

        // Any future type: a defensive placeholder (no real campaign reaches this branch).
        return new PreparedCampaign
        {
            ShouldSend = true,
            Render = _ => new EmailContent(
                $"[Easy Notif] {campaign.Type} ({campaign.MailLanguage})",
                "<p>Placeholder content, replaced in a later phase.</p>",
                "Placeholder content, replaced in a later phase.")
        };
    }
}
