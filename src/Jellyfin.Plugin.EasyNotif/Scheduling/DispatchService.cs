using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Scheduling;

/// <summary>
/// Evaluates the scheduled campaigns and sends what is due. See <see cref="DispatchService"/>.
/// </summary>
public interface IDispatchService
{
    /// <summary>
    /// Runs every campaign whose <see cref="Campaign.NextRunUtc"/> is due. Returns immediately, with
    /// no I/O beyond the in-memory campaign list, when nothing is due (R13).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the due campaigns have run.</returns>
    Task RunDueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs one campaign now, whether or not it is enabled or due, advancing its
    /// <see cref="Campaign.LastSentUtc"/> and <see cref="Campaign.NextRunUtc"/>. This is an explicit
    /// admin action, so it is not idempotency-keyed: every click sends, even within the same due
    /// slot (unlike the scheduled path, which keys each occurrence to survive a crash-retry).
    /// </summary>
    /// <param name="campaignId">The campaign id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run summary; <see cref="CampaignRunResult.Found"/> is false for an unknown id.</returns>
    Task<CampaignRunResult> RunCampaignNowAsync(string campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the current content of one campaign to a single address as a preview, without touching
    /// <see cref="Campaign.LastSentUtc"/> or <see cref="Campaign.NextRunUtc"/>.
    /// </summary>
    /// <param name="campaignId">The campaign id.</param>
    /// <param name="toEmail">The address to send the preview to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preview outcome; <see cref="CampaignPreviewResult.Found"/> is false for an unknown id.</returns>
    Task<CampaignPreviewResult> PreviewAsync(string campaignId, string toEmail, CancellationToken cancellationToken);
}

/// <summary>The outcome of a campaign preview send.</summary>
/// <param name="Found">Whether the campaign exists.</param>
/// <param name="Sent">Whether the provider accepted the preview.</param>
/// <param name="Movies">Movies in the previewed digest.</param>
/// <param name="Series">Series in the previewed digest.</param>
public sealed record CampaignPreviewResult(bool Found, bool Sent, int Movies, int Series);

/// <summary>The outcome of running one campaign.</summary>
/// <param name="CampaignId">The campaign id.</param>
/// <param name="Sent">Messages the provider accepted.</param>
/// <param name="Failed">Messages the provider rejected.</param>
/// <param name="Skipped">Eligible-by-category users skipped for want of a valid address.</param>
/// <param name="NextRunUtc">The campaign's next due time after the run.</param>
public sealed record CampaignRunResult(string CampaignId, int Sent, int Failed, int Skipped, DateTime? NextRunUtc)
{
    /// <summary>Gets a value indicating whether the campaign existed.</summary>
    public bool Found { get; init; }
}

/// <inheritdoc cref="IDispatchService"/>
public sealed class DispatchService : IDispatchService
{
    private readonly ICampaignStore _campaigns;
    private readonly IPreferenceService _preferences;
    private readonly IEmailComposer _composer;
    private readonly IEmailSender _sender;
    private readonly IQuotaGuard _quota;
    private readonly ISendLog _sendLog;
    private readonly IConfigAccessor _config;
    private readonly IEasyNotifLog _easyNotifLog;
    private readonly ILogger<DispatchService> _logger;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="DispatchService"/> class.</summary>
    /// <param name="campaigns">The campaign store.</param>
    /// <param name="preferences">The preference service (recipient resolution).</param>
    /// <param name="composer">The campaign email composer.</param>
    /// <param name="sender">The email transport.</param>
    /// <param name="quota">The send quota guard.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    /// <param name="logger">Logger.</param>
    public DispatchService(
        ICampaignStore campaigns,
        IPreferenceService preferences,
        IEmailComposer composer,
        IEmailSender sender,
        IQuotaGuard quota,
        ISendLog sendLog,
        IConfigAccessor config,
        IEasyNotifLog easyNotifLog,
        ILogger<DispatchService> logger)
        : this(campaigns, preferences, composer, sender, quota, sendLog, config, easyNotifLog, logger, () => DateTime.UtcNow)
    {
    }

    internal DispatchService(
        ICampaignStore campaigns,
        IPreferenceService preferences,
        IEmailComposer composer,
        IEmailSender sender,
        IQuotaGuard quota,
        ISendLog sendLog,
        IConfigAccessor config,
        IEasyNotifLog easyNotifLog,
        ILogger<DispatchService> logger,
        Func<DateTime> now)
    {
        _campaigns = campaigns;
        _preferences = preferences;
        _composer = composer;
        _sender = sender;
        _quota = quota;
        _sendLog = sendLog;
        _config = config;
        _easyNotifLog = easyNotifLog;
        _logger = logger;
        _now = now;
    }

    /// <inheritdoc/>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _easyNotifLog.Debug("dispatch.skipped", new Dictionary<string, object?> { ["reason"] = "already-running" });
            return;
        }

        try
        {
            var now = _now();
            var due = _campaigns.All()
                .Where(c => c.Enabled && c.NextRunUtc is { } next && next <= now)
                .ToList();
            if (due.Count == 0)
            {
                return;
            }

            var cfg = _config.Get();
            var tz = ResolveTimeZone(cfg.SchedulerTimeZone);
            foreach (var campaign in due)
            {
                await RunCampaignAsync(campaign, now, tz, cfg, manualRun: false, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<CampaignRunResult> RunCampaignNowAsync(string campaignId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var campaign = _campaigns.Get(campaignId);
            if (campaign is null)
            {
                return new CampaignRunResult(campaignId, 0, 0, 0, null) { Found = false };
            }

            var now = _now();
            var cfg = _config.Get();
            return await RunCampaignAsync(campaign, now, ResolveTimeZone(cfg.SchedulerTimeZone), cfg, manualRun: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<CampaignPreviewResult> PreviewAsync(string campaignId, string toEmail, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var campaign = _campaigns.Get(campaignId);
            if (campaign is null)
            {
                return new CampaignPreviewResult(false, false, 0, 0);
            }

            if (_quota.Snapshot().Over)
            {
                _easyNotifLog.Warn("dispatch.skipped", new Dictionary<string, object?>
                {
                    ["campaignId"] = campaign.Id,
                    ["reason"] = "quota-over-preview"
                });
                return new CampaignPreviewResult(true, false, 0, 0);
            }

            var now = _now();
            var cfg = _config.Get();
            var prepared = await _composer.PrepareAsync(campaign, now, cancellationToken, freshWindow: true).ConfigureAwait(false);
            var content = prepared.Render(new Recipient(Guid.Empty, toEmail));

            var result = await _sender.SendAsync(
                new EmailMessage
                {
                    To = toEmail,
                    Subject = "[Preview] " + content.Subject,
                    Html = content.Html,
                    Text = content.Text,
                    ReplyTo = cfg.ReplyTo,
                    Tags = [new EmailTag("context", "preview"), new EmailTag("campaignId", campaign.Id)],
                    Attachments = content.Attachments,
                    IdempotencyKey = null
                },
                cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                _quota.RecordSend();
            }

            try
            {
                _sendLog.Append(new SendLogEntry
                {
                    Ts = now,
                    Context = "preview",
                    Category = campaign.Category.ToString(),
                    ToMasked = EmailMasker.Mask(toEmail),
                    Subject = content.Subject,
                    ResendId = result.ResendId,
                    Status = result.Success ? "sent" : "failed"
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "[EasyNotif] Could not record a campaign preview in the send log.");
            }

            return new CampaignPreviewResult(true, result.Success, prepared.MovieCount, prepared.SeriesCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    private TimeZoneInfo ResolveTimeZone(string? ianaId)
    {
        var tz = RecurrenceSchedule.ResolveTimeZone(ianaId);
        if (tz.Equals(TimeZoneInfo.Utc) && !string.IsNullOrWhiteSpace(ianaId) && ianaId.Trim() != "UTC")
        {
            _logger.LogWarning("[EasyNotif] Unknown scheduler time zone \"{TimeZone}\"; using UTC.", ianaId);
        }

        return tz;
    }

    private async Task<CampaignRunResult> RunCampaignAsync(
        Campaign campaign,
        DateTime now,
        TimeZoneInfo tz,
        PluginConfiguration cfg,
        bool manualRun,
        CancellationToken cancellationToken)
    {
        // For a scheduled run the idempotency key identifies this occurrence (the due slot, before it
        // is advanced), so a crash-retry does not re-send but a later occurrence does. A manual "run
        // now" is an explicit admin action with no key: it always sends, even a second time within
        // the same slot (the slot barely moves for a daily cadence).
        var runSlot = (campaign.NextRunUtc ?? now).ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(cfg.ResendApiKey) || string.IsNullOrWhiteSpace(cfg.FromEmail))
        {
            _easyNotifLog.Warn("dispatch.skipped", new Dictionary<string, object?>
            {
                ["campaignId"] = campaign.Id,
                ["reason"] = "transport-not-configured"
            });
            return new CampaignRunResult(campaign.Id, 0, 0, 0, campaign.NextRunUtc) { Found = true };
        }

        if (_quota.Snapshot().Over)
        {
            _easyNotifLog.Warn("dispatch.skipped", new Dictionary<string, object?>
            {
                ["campaignId"] = campaign.Id,
                ["reason"] = "quota-over"
            });
            return new CampaignRunResult(campaign.Id, 0, 0, 0, campaign.NextRunUtc) { Found = true };
        }

        var prepared = await _composer.PrepareAsync(campaign, now, cancellationToken).ConfigureAwait(false);
        if (!prepared.ShouldSend)
        {
            var skippedNext = campaign.Schedule.NextRunUtc(now, tz);
            _campaigns.Update(campaign.Id, c =>
            {
                c.LastSentUtc = now;
                c.NextRunUtc = skippedNext;
            });
            _easyNotifLog.Info("dispatch.campaign", new Dictionary<string, object?>
            {
                ["campaignId"] = campaign.Id,
                ["sent"] = 0,
                ["skipped"] = prepared.SkipReason,
                ["nextRunUtc"] = skippedNext
            });
            return new CampaignRunResult(campaign.Id, 0, 0, 0, skippedNext) { Found = true };
        }

        IReadOnlyList<Recipient> recipients;
        int withoutEmail;
        try
        {
            recipients = _preferences.GetRecipients(campaign.Category);
            withoutEmail = _preferences.GetAllForAdmin()
                .Count(r => !r.HasEmail && r.Categories.GetValueOrDefault(campaign.Category));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _easyNotifLog.Error("dispatch.error", new Dictionary<string, object?> { ["campaignId"] = campaign.Id }, ex);
            return new CampaignRunResult(campaign.Id, 0, 0, 0, campaign.NextRunUtc) { Found = true };
        }

        _easyNotifLog.Info("dispatch.resolved", new Dictionary<string, object?>
        {
            ["campaignId"] = campaign.Id,
            ["category"] = campaign.Category.ToString(),
            ["eligible"] = recipients.Count,
            ["excludedNoEmail"] = withoutEmail
        });

        var sent = 0;
        var failed = 0;
        var deduplicated = 0;
        var quotaStoppedMidRun = false;

        foreach (var recipient in recipients)
        {
            if (_quota.Snapshot().Over)
            {
                quotaStoppedMidRun = true;
                _easyNotifLog.Warn("dispatch.skipped", new Dictionary<string, object?>
                {
                    ["campaignId"] = campaign.Id,
                    ["reason"] = "quota-over-mid-run",
                    ["sent"] = sent
                });
                break;
            }

            var content = prepared.Render(recipient);
            var category = campaign.Category.ToString().ToLowerInvariant();
            var message = new EmailMessage
            {
                To = recipient.Email,
                Subject = content.Subject,
                Html = content.Html,
                Text = content.Text,
                ReplyTo = cfg.ReplyTo,
                Headers = UnsubscribeToken.BuildListUnsubscribeHeaders(cfg.PublicServerUrl, cfg.UnsubscribeSecret, recipient.UserId, category),
                Tags =
                [
                    new EmailTag("context", campaign.Id),
                    new EmailTag("campaignId", campaign.Id),
                    new EmailTag("category", campaign.Category.ToString())
                ],
                Attachments = content.Attachments,
                IdempotencyKey = manualRun ? null : $"{campaign.Id}:{runSlot}:{recipient.UserId:N}"
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
                deduplicated++;
                status = "deduplicated";
            }
            else
            {
                failed++;
                status = "failed";
            }

            try
            {
                _sendLog.Append(new SendLogEntry
                {
                    Ts = now,
                    Context = campaign.Id,
                    Category = campaign.Category.ToString(),
                    ToMasked = EmailMasker.Mask(recipient.Email),
                    Subject = content.Subject,
                    ResendId = result.ResendId,
                    Status = status
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "[EasyNotif] Could not record a campaign send in the send log.");
            }
        }

        DateTime? next = campaign.NextRunUtc;
        if (!quotaStoppedMidRun)
        {
            var advanced = campaign.Schedule.NextRunUtc(now, tz);
            _campaigns.Update(campaign.Id, c =>
            {
                c.LastSentUtc = now;
                c.NextRunUtc = advanced;
            });
            next = advanced;
        }

        _easyNotifLog.Info("dispatch.campaign", new Dictionary<string, object?>
        {
            ["campaignId"] = campaign.Id,
            ["sent"] = sent,
            ["failed"] = failed,
            ["deduplicated"] = deduplicated,
            ["nextRunUtc"] = next
        });

        return new CampaignRunResult(campaign.Id, sent, failed, withoutEmail, next) { Found = true };
    }
}
