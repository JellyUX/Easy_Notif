using System.Net.Mail;
using System.Reflection;
using Jellyfin.Plugin.EasyNotif.Alerts;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Inject;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Util;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Controllers;

/// <summary>
/// HTTP API for Easy Notif. Route: <c>/EasyNotif</c>.
/// <para>
/// The <c>me/*</c> endpoints act on the caller only - the identity comes from
/// <see cref="IAuthorizationContext"/> and no endpoint takes a user id. The <c>admin/*</c> endpoints
/// require elevation.
/// </para>
/// </summary>
/// <remarks>
/// Public contract, covered by SemVer from 1.0.0: the <c>/EasyNotif</c> routes, their verbs and
/// their status codes, plus the <c>Schema</c> integer on the persisted documents
/// (<c>preferences.json</c>, <c>campaigns.json</c>, <c>quota.json</c>, <c>added-items.json</c>,
/// <c>playback-history.json</c>, <c>sent-keys.json</c>, <c>retry-queue.json</c>). A breaking change to either needs a major version bump.
/// <c>send-log.jsonl</c> is an append-only, self-healing diagnostic log and is not part of that
/// guarantee. Authenticated actions can also return <c>401</c> / <c>403</c> from Jellyfin's auth
/// middleware; those are declared once at the class level.
/// </remarks>
[ApiController]
[Route("EasyNotif")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public class EasyNotifController : ControllerBase
{
    private static readonly Assembly PluginAssembly = typeof(EasyNotifController).Assembly;

    // The admin banner shows while an incident is younger than this (DEC-64).
    private static readonly TimeSpan BannerWindow = TimeSpan.FromHours(24);

    private readonly IPreferenceService _preferences;
    private readonly IFollowedSeriesService _followedSeries;
    private readonly ILibraryCatalog _libraries;
    private readonly IConfigAccessor _config;
    private readonly ISecretStore _secrets;
    private readonly IAuthorizationContext _authContext;
    private readonly IEmailSender _emailSender;
    private readonly IQuotaGuard _quota;
    private readonly SendCooldown _sendCooldown;
    private readonly ISendLog _sendLog;
    private readonly IManualEmailService _manualEmail;
    private readonly IManualSendQueue _manualQueue;
    private readonly IScheduledAnnouncementStore _announcements;
    private readonly ICampaignStore _campaigns;
    private readonly IDispatchService _dispatch;
    private readonly ITemplateStore _templates;
    private readonly IFileTransformationDetector _fileTransformation;
    private readonly IDrainStatus _drains;
    private readonly IIncidentStore _incidents;
    private readonly IEasyNotifLog _easyNotifLog;
    private readonly ILogger<EasyNotifController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EasyNotifController"/> class.
    /// </summary>
    /// <param name="preferences">The preference service.</param>
    /// <param name="followedSeries">The followed-series service.</param>
    /// <param name="libraries">The libraries behind the newsletter library choice.</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="secrets">The transport secret store.</param>
    /// <param name="authContext">Jellyfin request authorization context.</param>
    /// <param name="emailSender">The email transport.</param>
    /// <param name="quota">The send quota guard.</param>
    /// <param name="sendCooldown">The per-user cooldown for the self-test send.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="manualEmail">The manual admin email service.</param>
    /// <param name="manualQueue">The background queue of manual email lots.</param>
    /// <param name="announcements">The scheduled announcement store.</param>
    /// <param name="campaigns">The campaign store.</param>
    /// <param name="dispatch">The dispatch service.</param>
    /// <param name="templates">The email template store.</param>
    /// <param name="fileTransformation">The FileTransformation availability detector.</param>
    /// <param name="drains">The health of the background drains.</param>
    /// <param name="incidents">The incidents shown in the admin banner.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    /// <param name="logger">Logger.</param>
    public EasyNotifController(
        IPreferenceService preferences,
        IFollowedSeriesService followedSeries,
        ILibraryCatalog libraries,
        IConfigAccessor config,
        ISecretStore secrets,
        IAuthorizationContext authContext,
        IEmailSender emailSender,
        IQuotaGuard quota,
        SendCooldown sendCooldown,
        ISendLog sendLog,
        IManualEmailService manualEmail,
        IManualSendQueue manualQueue,
        IScheduledAnnouncementStore announcements,
        ICampaignStore campaigns,
        IDispatchService dispatch,
        ITemplateStore templates,
        IFileTransformationDetector fileTransformation,
        IDrainStatus drains,
        IIncidentStore incidents,
        IEasyNotifLog easyNotifLog,
        ILogger<EasyNotifController> logger)
    {
        _preferences = preferences;
        _followedSeries = followedSeries;
        _libraries = libraries;
        _config = config;
        _secrets = secrets;
        _authContext = authContext;
        _emailSender = emailSender;
        _quota = quota;
        _sendCooldown = sendCooldown;
        _sendLog = sendLog;
        _manualEmail = manualEmail;
        _manualQueue = manualQueue;
        _announcements = announcements;
        _campaigns = campaigns;
        _dispatch = dispatch;
        _templates = templates;
        _fileTransformation = fileTransformation;
        _drains = drains;
        _incidents = incidents;
        _easyNotifLog = easyNotifLog;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Current user
    // -------------------------------------------------------------------------

    /// <summary>Gets the caller's category opt-in state.</summary>
    /// <returns>An object with a boolean per category.</returns>
    [HttpGet("me/preferences")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetMyPreferences()
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() =>
        {
            var pref = _preferences.GetOrCreate(userId);
            var excluded = pref.ExcludedLibraries ?? [];
            return Ok(new
            {
                news = pref.Categories.GetValueOrDefault(EmailCategory.News),
                recap = pref.Categories.GetValueOrDefault(EmailCategory.Recap),
                announcements = !pref.AnnouncementsOptOut,
                episodes = !pref.EpisodesOptOut,
                emailRejected = pref.ContactEmailRejectedUtc is not null,
                libraries = _libraries.NewsletterLibrariesFor(userId)
                    .Select(l => new { id = l.Id, name = l.Name, selected = !excluded.Contains(l.Id) })
                    .ToList()
            });
        });
    }

    /// <summary>
    /// Sets the caller's category opt-in state and newsletter libraries. Omitted fields are left
    /// unchanged.
    /// </summary>
    /// <param name="body">The categories to change.</param>
    /// <returns>204 on success; 400 <c>no-library</c> when <c>libraries</c> keeps none of the caller's
    /// libraries (DEC-76); 503 when storage is unavailable.</returns>
    [HttpPut("me/preferences")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> PutMyPreferences([FromBody] CategoryUpdate body)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() =>
        {
            if (body?.Libraries is { } selected)
            {
                // Stored as exclusions so a library created later is received (DEC-74); the
                // libraries the caller cannot see are dropped from the stored list. An empty list
                // (unknown user, lookup error) leaves the stored choice untouched.
                var visible = _libraries.NewsletterLibrariesFor(userId).Select(l => l.Id).ToList();
                if (visible.Count > 0 && !visible.Any(selected.Contains))
                {
                    return BadRequest(new { error = "no-library" });
                }

                if (visible.Count > 0)
                {
                    _preferences.SetExcludedLibraries(userId, visible.Where(id => !selected.Contains(id)).ToList());
                }
            }

            _preferences.SetCategories(
                userId,
                BuildCategoryMap(body),
                body?.Announcements is { } on ? !on : null,
                body?.Episodes is { } episodes ? !episodes : null);
            return NoContent();
        });
    }

    /// <summary>Gets the caller's own contact address.</summary>
    /// <returns>An object with the address, or null.</returns>
    [HttpGet("me/contact-email")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetMyContactEmail()
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() => Ok(new { email = _preferences.GetOrCreate(userId).ContactEmail }));
    }

    /// <summary>Sets or clears the caller's contact address.</summary>
    /// <param name="body">The address, or null/blank to clear it.</param>
    /// <returns>204 on success; 400 when the address is not valid; 503 when storage is unavailable.</returns>
    [HttpPut("me/contact-email")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> PutMyContactEmail([FromBody] ContactEmailUpdate body)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() =>
        {
            try
            {
                _preferences.SetContactEmail(userId, body?.Email);
                return NoContent();
            }
            catch (ArgumentException)
            {
                return BadRequest();
            }
        });
    }

    /// <summary>Sends the caller a test email to their contact address.</summary>
    /// <param name="body">Optional request body carrying the UI language.</param>
    /// <returns>200 with <c>{ ok }</c>; 400 when the caller has no contact address; 429 when the
    /// per-user cooldown is still running; 503 when the send quota is exhausted or storage is
    /// unavailable.</returns>
    [HttpPost("me/test")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> SendMyTestEmail([FromBody] TestEmailRequest? body)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);

        string? address;
        try
        {
            address = _preferences.GetOrCreate(userId).ContactEmail;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] A storage or configuration operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return BadRequest(new { error = "no-contact-email" });
        }

        if (!_sendCooldown.TryConsume(userId))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "cooldown" });
        }

        if (_quota.Snapshot().Over)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "quota-exceeded" });
        }

        var lang = string.Equals(body?.Lang, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        var (subject, html) = TestEmail.Build(lang);

        var result = await _emailSender.SendAsync(
            new EmailMessage
            {
                To = address,
                Subject = subject,
                Html = html,
                ReplyTo = _config.Get().ReplyTo,
                IdempotencyKey = $"test:{userId:N}:{Guid.NewGuid():N}"
            },
            HttpContext.RequestAborted).ConfigureAwait(false);

        if (result.Success)
        {
            _quota.RecordSend();
        }

        try
        {
            _sendLog.Append(new SendLogEntry
            {
                Ts = DateTime.UtcNow,
                Context = "test",
                ToMasked = EmailMasker.Mask(address),
                UserId = userId,
                Subject = subject,
                ResendId = result.ResendId,
                Status = result.Success ? "sent" : "failed"
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "[EasyNotif] Could not record the test send in the send log.");
        }

        return Ok(new { ok = result.Success });
    }

    // -------------------------------------------------------------------------
    // Current user - followed series
    // -------------------------------------------------------------------------

    /// <summary>Lists the series the caller follows and can still see.</summary>
    /// <returns>200 with <c>[{ id, name, hasImage }]</c>; 503 when storage is unavailable.</returns>
    [HttpGet("me/series")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetMySeries()
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() => Ok(_followedSeries.List(userId)
            .Select(s => new { id = s.Id, name = s.Name, hasImage = s.HasImage })));
    }

    /// <summary>Gets whether the caller follows a series.</summary>
    /// <param name="seriesId">The series id.</param>
    /// <returns>200 with <c>{ following }</c>; 404, the same for a missing or a forbidden series;
    /// 503 when storage is unavailable.</returns>
    [HttpGet("me/series/{seriesId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetMySeriesState([FromRoute] Guid seriesId)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() => _followedSeries.IsFollowing(userId, seriesId) is { } following
            ? Ok(new { following })
            : NotFound());
    }

    /// <summary>Follows a series.</summary>
    /// <param name="seriesId">The series id.</param>
    /// <param name="source">Where the request comes from: <c>bell</c> (default) or <c>panel</c>.</param>
    /// <returns>204; 404, the same for a missing or a forbidden series; 503 when storage is unavailable.</returns>
    [HttpPut("me/series/{seriesId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> FollowSeries([FromRoute] Guid seriesId, [FromQuery] string? source)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        var origin = source == "panel" ? "panel" : "bell";
        return Wrap(() => _followedSeries.Follow(userId, seriesId, origin) ? NoContent() : NotFound());
    }

    /// <summary>Stops following a series.</summary>
    /// <param name="seriesId">The series id.</param>
    /// <returns>204; 404, the same for a missing or a forbidden series the caller does not follow;
    /// 503 when storage is unavailable.</returns>
    [HttpDelete("me/series/{seriesId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> UnfollowSeries([FromRoute] Guid seriesId)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() => _followedSeries.Unfollow(userId, seriesId) ? NoContent() : NotFound());
    }

    // -------------------------------------------------------------------------
    // Admin - preferences
    // -------------------------------------------------------------------------

    /// <summary>Gets one row per Jellyfin user for the admin table. Administrators only.</summary>
    /// <returns>The rows, with contact addresses masked.</returns>
    [HttpGet("admin/preferences")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetAllPreferences() => Wrap(() =>
    {
        var rows = _preferences.GetAllForAdmin().Select(r => new
        {
            userId = r.UserId,
            userName = r.UserName,
            maskedEmail = r.MaskedEmail,
            hasEmail = r.HasEmail,
            news = r.Categories.GetValueOrDefault(EmailCategory.News),
            recap = r.Categories.GetValueOrDefault(EmailCategory.Recap),
            updatedAt = r.UpdatedAt
        });
        return Ok(rows);
    });

    /// <summary>Sets another user's preferences. Administrators only.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="body">The fields to change. A null field is left unchanged.</param>
    /// <returns>204 on success; 400 when the address is not valid; 503 when storage is unavailable.</returns>
    [HttpPut("admin/preferences/{userId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult PutUserPreferences([FromRoute] Guid userId, [FromBody] AdminPreferenceUpdate body)
    {
        return Wrap(() =>
        {
            try
            {
                if (body?.ContactEmail is not null)
                {
                    _preferences.SetContactEmail(userId, body.ContactEmail);
                }

                var categories = BuildCategoryMap(body);
                if (categories.Count > 0)
                {
                    _preferences.SetCategories(userId, categories);
                }

                return NoContent();
            }
            catch (ArgumentException)
            {
                return BadRequest();
            }
        });
    }

    // -------------------------------------------------------------------------
    // Admin - settings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Gets the plugin settings. Secret values (the Resend API key, the webhook signing secret) are
    /// never returned - only a boolean saying whether each one is set. Administrators only.
    /// </summary>
    /// <returns>The non-secret settings plus the "is set" flags.</returns>
    [HttpGet("admin/settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetSettings() => Wrap(() =>
    {
        var cfg = _config.Get();
        var secrets = _secrets.Get();
        return Ok(new
        {
            fromEmail = cfg.FromEmail,
            fromName = cfg.FromName,
            replyTo = cfg.ReplyTo,
            publicServerUrl = cfg.PublicServerUrl,
            schedulerTimeZone = cfg.SchedulerTimeZone,
            newsletterMaxMovies = cfg.NewsletterMaxMovies,
            newsletterMaxSeries = cfg.NewsletterMaxSeries,
            quotaDailyLimit = cfg.QuotaDailyLimit,
            quotaMonthlyLimit = cfg.QuotaMonthlyLimit,
            injectWebUi = cfg.InjectWebUi,
            injectSeriesBell = cfg.InjectSeriesBell,
            alertEmail = cfg.AlertEmail,
            alertLanguage = cfg.AlertLanguage,
            resendApiKeySet = !string.IsNullOrEmpty(secrets.ResendApiKey),
            webhookSigningSecretSet = !string.IsNullOrEmpty(secrets.WebhookSigningSecret),
            startupWarning = cfg.StartupWarning
        });
    });

    /// <summary>
    /// Updates the plugin settings. A blank secret leaves the stored one untouched. Administrators only.
    /// </summary>
    /// <param name="body">The settings.</param>
    /// <returns>204 on success; 400 on an invalid time zone, address, public server URL, newsletter
    /// cap or quota limit; 503 when the plugin instance is unavailable.</returns>
    [HttpPut("admin/settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult PutSettings([FromBody] SettingsUpdate body)
    {
        if (body is null)
        {
            return BadRequest();
        }

        if (!string.IsNullOrWhiteSpace(body.SchedulerTimeZone)
            && !TimeZoneInfo.TryFindSystemTimeZoneById(body.SchedulerTimeZone.Trim(), out _))
        {
            return BadRequest();
        }

        foreach (var address in new[] { body.FromEmail, body.ReplyTo, body.AlertEmail })
        {
            if (!string.IsNullOrWhiteSpace(address) && !MailAddress.TryCreate(address.Trim(), out _))
            {
                return BadRequest();
            }
        }

        if (body.AlertLanguage is not null and not "fr" and not "en")
        {
            return BadRequest();
        }

        if (!string.IsNullOrWhiteSpace(body.PublicServerUrl) && !PublicUrl.IsValid(body.PublicServerUrl.Trim()))
        {
            return BadRequest();
        }

        foreach (var cap in new[] { body.NewsletterMaxMovies, body.NewsletterMaxSeries })
        {
            if (cap is < 1 or > PluginConfiguration.MaxNewsletterCap)
            {
                return BadRequest();
            }
        }

        if (body.QuotaDailyLimit is < 0 or > PluginConfiguration.MaxQuotaDailyLimit
            || body.QuotaMonthlyLimit is < 1 or > PluginConfiguration.MaxQuotaMonthlyLimit)
        {
            return BadRequest();
        }

        return Wrap(() =>
        {
            var cfg = _config.Get();
            cfg.FromEmail = Trimmed(body.FromEmail);
            cfg.FromName = Trimmed(body.FromName);
            cfg.ReplyTo = Trimmed(body.ReplyTo);
            cfg.PublicServerUrl = Trimmed(body.PublicServerUrl);
            cfg.NewsletterMaxMovies = body.NewsletterMaxMovies ?? cfg.NewsletterMaxMovies;
            cfg.NewsletterMaxSeries = body.NewsletterMaxSeries ?? cfg.NewsletterMaxSeries;
            cfg.QuotaDailyLimit = body.QuotaDailyLimit ?? cfg.QuotaDailyLimit;
            cfg.QuotaMonthlyLimit = body.QuotaMonthlyLimit ?? cfg.QuotaMonthlyLimit;
            cfg.InjectWebUi = body.InjectWebUi ?? cfg.InjectWebUi;
            cfg.InjectSeriesBell = body.InjectSeriesBell ?? cfg.InjectSeriesBell;
            cfg.AlertLanguage = body.AlertLanguage ?? cfg.AlertLanguage;
            if (body.AlertEmail is not null)
            {
                cfg.AlertEmail = Trimmed(body.AlertEmail);
            }

            if (!string.IsNullOrWhiteSpace(body.SchedulerTimeZone))
            {
                cfg.SchedulerTimeZone = body.SchedulerTimeZone.Trim();
            }

            if (!string.IsNullOrWhiteSpace(body.ResendApiKey))
            {
                _secrets.SetResendApiKey(body.ResendApiKey.Trim());
            }

            if (!string.IsNullOrWhiteSpace(body.WebhookSigningSecret))
            {
                _secrets.SetWebhookSigningSecret(body.WebhookSigningSecret.Trim());
            }

            _config.Save();
            var secrets = _secrets.Get();
            _easyNotifLog.Info("settings.updated", new Dictionary<string, object?>
            {
                ["fromEmail"] = cfg.FromEmail,
                ["resendApiKeySet"] = !string.IsNullOrEmpty(secrets.ResendApiKey),
                ["webhookSigningSecretSet"] = !string.IsNullOrEmpty(secrets.WebhookSigningSecret)
            });
            return NoContent();
        });
    }

    /// <summary>
    /// Gets the Resend transport status for the settings tab: whether it is configured, the rolling
    /// send quota with its configured limits and the share reserved for a campaign due today, the
    /// last webhook received, the incidents of the last 7 days with the banner flag (one younger
    /// than 24 hours), each campaign's last error, the health of the background drains
    /// (last write, lost batches, dropped items, queue depth), and the last few sends. Never
    /// returns a secret.
    /// Administrators only.
    /// </summary>
    /// <returns>The status object.</returns>
    [HttpGet("admin/status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetStatus() => Wrap(() =>
    {
        var cfg = _config.Get();
        var (lastWebhookUtc, lastWebhookType) = _sendLog.LastWebhook();
        var quota = _quota.Snapshot();
        var tz = RecurrenceSchedule.ResolveTimeZone(cfg.SchedulerTimeZone);
        var incidents = _incidents.All();
        var now = DateTime.UtcNow;
        return Ok(new
        {
            configured =!string.IsNullOrWhiteSpace(_secrets.Get().ResendApiKey) && !string.IsNullOrWhiteSpace(cfg.FromEmail),
            fromEmail = cfg.FromEmail,
            publicUrlSet = !string.IsNullOrWhiteSpace(cfg.PublicServerUrl),
            fileTransformation = _fileTransformation.IsAvailable(),
            quota = new
            {
                last30d = quota.Last30d,
                dailyToday = quota.DailyToday,
                monthlyLimit = quota.MonthlyLimit,
                dailyLimit = quota.DailyLimit,
                warn80 = quota.Warn80,
                over = quota.Over,
                suspendedUntilUtc = quota.SuspendedUntilUtc,
                reserve = QuotaBudget.Reserve(quota.DailyLimit, _campaigns.All(), DateTime.UtcNow)
            },
            lastWebhookUtc,
            lastWebhookType,
            campaigns = _campaigns.All().Select(c => new
            {
                id = c.Id,
                enabled = c.Enabled,
                templateId = c.TemplateId,
                nextRunUtc = c.NextRunUtc,
                lastSentUtc = c.LastSentUtc,
                nextRunLocal = ToConfiguredLocal(c.NextRunUtc, tz),
                lastSentLocal = ToConfiguredLocal(c.LastSentUtc, tz),
                lastError = c.LastError,
                lastErrorUtc = c.LastErrorUtc
            }),
            drains = _drains.Snapshot().ToDictionary(d => d.Key, d => new
            {
                lastFlushUtc = d.Value.LastFlushUtc,
                lostBatches = d.Value.LostBatches,
                lostItems = d.Value.LostItems,
                dropped = d.Value.Dropped,
                depth = d.Value.Depth
            }),
            banner = incidents.Any(i => i.Ts >= now - BannerWindow),
            alertEmailSet = !string.IsNullOrWhiteSpace(cfg.AlertEmail),
            lastDigestUtc = _incidents.LastDigestUtc,
            incidents = incidents.OrderByDescending(i => i.Ts).Take(50).Select(i => new
            {
                ts = i.Ts,
                type = i.Type,
                detail = i.Detail
            }),
            recent = _sendLog.Recent(10).Select(e => new
            {
                ts = e.Ts,
                context = e.Context,
                toMasked = e.ToMasked,
                category = e.Category,
                subject = e.Subject,
                status = e.Status
            })
        });
    });

    /// <summary>Gets the last lines of the plugin's dedicated log file. Administrators only.</summary>
    /// <param name="tail">How many lines to return at most (default 200, clamped 1-2000).</param>
    /// <returns>
    /// The lines, oldest first; an empty array when no log file exists yet or the directory cannot
    /// be read. This endpoint never fails: <see cref="IEasyNotifLog.Tail"/> swallows its own I/O.
    /// </returns>
    [HttpGet("admin/logs")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLogs([FromQuery] int tail = 200)
        => Ok(_easyNotifLog.Tail(Math.Clamp(tail, 1, 2000)));

    /// <summary>
    /// Sends a one-off manual email (plain text or HTML, with optional attachments) to all users
    /// with a contact address, a chosen subset, or a single test address. Not subject to category
    /// preferences. Administrators only.
    /// </summary>
    /// <remarks>Deprecated: the send is tied to the HTTP request, so closing the tab stops it. Use
    /// <c>POST admin/send/batches</c>, which runs in the background. Kept for API compatibility.</remarks>
    /// <param name="body">The composed email and its audience.</param>
    /// <returns>200 with a per-recipient summary; 400 on an invalid request; 503 on a storage failure.</returns>
    [HttpPost("admin/send")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> SendManualEmail([FromBody] ManualSendRequest? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Subject))
        {
            return BadRequest(new { error = "subject-required" });
        }

        if (_quota.Snapshot().Over)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "quota-exceeded" });
        }

        if (!TryBuildManualRequest(body, out var request))
        {
            return BadRequest(new { error = "attachment-invalid" });
        }

        try
        {
            var result = await _manualEmail.SendAsync(request, HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new
            {
                sent = result.Sent,
                failed = result.Failed,
                skippedNoEmail = result.SkippedNoEmail,
                details = result.Details.Select(d => new { maskedTo = d.MaskedTo, status = d.Status })
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] A storage or configuration operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Starts a one-off manual email as a background lot (same body and audience rules as
    /// <c>POST admin/send</c>). The lot keeps running if the caller goes away; follow it with
    /// <c>GET admin/send/batches/{id}</c>. Administrators only.
    /// </summary>
    /// <param name="body">The composed email and its audience.</param>
    /// <returns>202 with the lot; 400 on an invalid request; 503 when the quota is exhausted or on a
    /// storage failure.</returns>
    [HttpPost("admin/send/batches")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult StartManualBatch([FromBody] ManualSendRequest? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Subject))
        {
            return BadRequest(new { error = "subject-required" });
        }

        if (_quota.Snapshot().Over)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "quota-exceeded" });
        }

        if (!TryBuildManualRequest(body, out var request))
        {
            return BadRequest(new { error = "attachment-invalid" });
        }

        try
        {
            var batch = _manualQueue.Start(request);
            return Accepted($"/EasyNotif/admin/send/batches/{batch.Id}", BatchDto(batch));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] A storage or configuration operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Gets the state of a manual lot. Administrators only.</summary>
    /// <param name="id">The lot id.</param>
    /// <returns>200 with the lot; 404 when it is unknown or expired (one hour after it finished).</returns>
    [HttpGet("admin/send/batches/{id:guid}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetManualBatch([FromRoute] Guid id)
        => _manualQueue.Get(id) is { } batch ? Ok(BatchDto(batch)) : NotFound();

    /// <summary>Gets the most recently started manual lot still held, so a reopened page can follow
    /// it. Administrators only.</summary>
    /// <returns>200 with the lot; 204 when none is held.</returns>
    [HttpGet("admin/send/batches/latest")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult GetLatestManualBatch()
        => _manualQueue.Latest() is { } batch ? Ok(BatchDto(batch)) : NoContent();

    /// <summary>
    /// Sends again the recipients of a finished manual lot whose send failed, with the same
    /// idempotency keys (no recipient gets the email twice). Administrators only.
    /// </summary>
    /// <param name="id">The lot id.</param>
    /// <returns>202 with the lot; 404 when it is unknown or expired; 409 while it runs or when no
    /// send failed; 503 when the quota is exhausted.</returns>
    [HttpPost("admin/send/batches/{id:guid}/retry")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult RetryManualBatch([FromRoute] Guid id)
    {
        if (_quota.Snapshot().Over)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "quota-exceeded" });
        }

        var (outcome, batch) = _manualQueue.Retry(id);
        return outcome switch
        {
            ManualRetryOutcome.Started => Accepted($"/EasyNotif/admin/send/batches/{id}", BatchDto(batch!)),
            ManualRetryOutcome.Running => Conflict(new { error = "batch-running" }),
            ManualRetryOutcome.NothingToRetry => Conflict(new { error = "nothing-to-retry" }),
            _ => NotFound()
        };
    }

    private static object BatchDto(ManualBatchSnapshot batch) => new
    {
        id = batch.Id,
        status = batch.Status,
        total = batch.Total,
        processed = batch.Details.Count,
        sent = batch.Sent,
        failed = batch.Failed,
        deduplicated = batch.Deduplicated,
        skippedNoEmail = batch.SkippedNoEmail,
        details = batch.Details.Select(d => new { maskedTo = d.MaskedTo, status = d.Status }),
        startedUtc = batch.StartedUtc,
        finishedUtc = batch.FinishedUtc
    };

    private static bool TryBuildManualRequest(ManualSendRequest body, out ManualEmailRequest request)
    {
        List<EmailAttachment> attachments;
        try
        {
            attachments = (body.Attachments ?? [])
                .Select(a => new EmailAttachment
                {
                    FileName = string.IsNullOrWhiteSpace(a.FileName) ? "attachment" : a.FileName!,
                    Content = Convert.FromBase64String(a.ContentBase64 ?? string.Empty),
                    ContentType = Trimmed(a.ContentType)
                })
                .ToList();
        }
        catch (FormatException)
        {
            request = null!;
            return false;
        }

        request = new ManualEmailRequest(
            body.Subject!.Trim(),
            body.Html,
            body.Text,
            Trimmed(body.RecipientMode) ?? "all",
            body.RecipientUserIds,
            Trimmed(body.TestAddress),
            attachments.Count > 0 ? attachments : null)
        {
            Wrap = body.Wrap == true,
            Lang = Trimmed(body.Lang) is "en" ? "en" : "fr"
        };
        return true;
    }

    /// <summary>
    /// Renders a manual email body inside the <c>generic</c> layout, for the sandboxed preview of the
    /// Manual email tab. Nothing is sent. Administrators only.
    /// </summary>
    /// <param name="body">The composed email; its audience only decides whether the unsubscribe link shows.</param>
    /// <returns>200 with <c>{ html }</c>; 400 when the body is missing or contains a script.</returns>
    [HttpPost("admin/send/preview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult PreviewManualEmail([FromBody] ManualSendRequest? body)
    {
        if (body is null)
        {
            return BadRequest(new { error = "body-required" });
        }

        body.Attachments = null;
        body.Subject = string.IsNullOrWhiteSpace(body.Subject) ? string.Empty : body.Subject;
        TryBuildManualRequest(body, out var request);
        try
        {
            return Ok(new { html = _manualEmail.Preview(request) });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] Could not render the manual email preview.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    // -------------------------------------------------------------------------
    // Admin - scheduled announcements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Programs a manual email for a date (FEAT-05). The date is read in the scheduler time zone
    /// (DEC-70); the dispatch tick sends it once, resolving the recipients at that time. No
    /// attachments and no test address (DEC-67). Administrators only.
    /// </summary>
    /// <param name="body">The composed email, its audience and its local send time.</param>
    /// <returns>201 with the announcement; 400 on an invalid request; 503 on a storage failure.</returns>
    [HttpPost("admin/announcements")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult ScheduleAnnouncement([FromBody] ScheduleAnnouncementRequest? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Subject))
        {
            return BadRequest(new { error = "subject-required" });
        }

        if (body.Attachments is { Count: > 0 })
        {
            return BadRequest(new { error = "attachments-not-allowed" });
        }

        var mode = Trimmed(body.RecipientMode) ?? "all";
        if (mode is not ("all" or "selected"))
        {
            return BadRequest(new { error = "invalid-mode" });
        }

        return Wrap(() =>
        {
            var tz = RecurrenceSchedule.ResolveTimeZone(_config.Get().SchedulerTimeZone);
            if (!DateTime.TryParseExact(
                    Trimmed(body.SendAt),
                    ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var local)
                || tz.IsInvalidTime(local))
            {
                return BadRequest(new { error = "invalid-send-at" });
            }

            var now = DateTime.UtcNow;
            var sendAtUtc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
            if (sendAtUtc <= now)
            {
                return BadRequest(new { error = "send-at-past" });
            }

            if (sendAtUtc > now.AddDays(366))
            {
                return BadRequest(new { error = "send-at-too-far" });
            }

            var announcement = new ScheduledAnnouncement
            {
                Id = Guid.NewGuid(),
                Subject = body.Subject.Trim(),
                Html = string.IsNullOrWhiteSpace(body.Html) ? null : body.Html,
                Text = string.IsNullOrWhiteSpace(body.Text) ? null : body.Text,
                Wrap = body.Wrap == true,
                Lang = Trimmed(body.Lang) is "en" ? "en" : "fr",
                Mode = mode,
                UserIds = mode == "selected" ? body.RecipientUserIds ?? [] : [],
                SendAtUtc = sendAtUtc,
                CreatedUtc = now
            };

            try
            {
                _manualEmail.Plan(announcement.ToRequest());
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            _announcements.Add(announcement);
            return Created($"/EasyNotif/admin/announcements/{announcement.Id}", AnnouncementDto(announcement, tz));
        });
    }

    /// <summary>Lists the scheduled announcements: upcoming ones and those finished in the last 30 days. Administrators only.</summary>
    /// <returns>200 with <c>{ timeZone, items }</c>, items soonest first; 503 on a storage failure.</returns>
    [HttpGet("admin/announcements")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetAnnouncements() => Wrap(() =>
    {
        var tz = RecurrenceSchedule.ResolveTimeZone(_config.Get().SchedulerTimeZone);
        return Ok(new { timeZone = tz.Id, items = _announcements.All().Select(a => AnnouncementDto(a, tz)) });
    });

    /// <summary>Cancels a scheduled announcement that has not been sent yet. Administrators only.</summary>
    /// <param name="id">The announcement id.</param>
    /// <returns>204 when cancelled; 404 when unknown; 409 when it is no longer pending; 503 on a storage failure.</returns>
    [HttpDelete("admin/announcements/{id:guid}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult CancelAnnouncement([FromRoute] Guid id) => Wrap(() =>
        _announcements.Cancel(id, DateTime.UtcNow) switch
        {
            CancelOutcome.Cancelled => NoContent(),
            CancelOutcome.NotPending => Conflict(new { error = "not-pending" }),
            _ => NotFound()
        });

    private static object AnnouncementDto(ScheduledAnnouncement a, TimeZoneInfo tz) => new
    {
        id = a.Id,
        subject = a.Subject,
        recipientMode = a.Mode,
        recipientCount = a.Mode == "selected" ? a.UserIds.Count : (int?)null,
        wrap = a.Wrap,
        lang = a.Lang,
        status = a.Status,
        sendAtUtc = a.SendAtUtc,
        sendAtLocal = ToConfiguredLocal(a.SendAtUtc, tz),
        nextAttemptLocal = ToConfiguredLocal(a.NextAttemptUtc, tz),
        timeZone = tz.Id,
        sent = a.SentCount,
        failed = a.FailedCount,
        skippedNoEmail = a.SkippedNoEmail,
        attempts = a.Attempts,
        finishedUtc = a.FinishedUtc
    };

    // -------------------------------------------------------------------------
    // Admin - campaigns
    // -------------------------------------------------------------------------

    /// <summary>Gets the scheduled campaigns and their state. Administrators only.</summary>
    /// <returns>The campaigns.</returns>
    [HttpGet("admin/campaigns")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetCampaigns() => Wrap(() =>
    {
        var tz = RecurrenceSchedule.ResolveTimeZone(_config.Get().SchedulerTimeZone);
        return Ok(_campaigns.All().Select(c => new
    {
        id = c.Id,
        type = c.Type.ToString(),
        category = c.Category.ToString(),
        enabled = c.Enabled,
        mailLanguage = c.MailLanguage,
        templateId = c.TemplateId,
        lastSentUtc = c.LastSentUtc,
        nextRunUtc = c.NextRunUtc,
        lastError = c.LastError,
        lastErrorUtc = c.LastErrorUtc,
        lastSentLocal = ToConfiguredLocal(c.LastSentUtc, tz),
        nextRunLocal = ToConfiguredLocal(c.NextRunUtc, tz),
        timeZone = tz.Id,
        schedule = new
        {
            kind = c.Schedule.Kind.ToString(),
            time = c.Schedule.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            dayOfWeek = c.Schedule.DayOfWeek?.ToString(),
            dayOfMonth = c.Schedule.DayOfMonth,
            intervalDays = c.Schedule.IntervalDays
        }
        }));
    });

    /// <summary>Updates one campaign's enabled state, mail language and/or schedule. Administrators only.</summary>
    /// <param name="id">The campaign id.</param>
    /// <param name="body">The fields to change. A null field is left unchanged.</param>
    /// <returns>204 on success; 400 on an invalid language or schedule; 404 for an unknown id.</returns>
    [HttpPut("admin/campaigns/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult PutCampaign([FromRoute] string id, [FromBody] CampaignUpdate? body)
    {
        if (body?.MailLanguage is { } lang && lang is not ("en" or "fr"))
        {
            return BadRequest(new { error = "invalid-language" });
        }

        RecurrenceSchedule? schedule = null;
        if (body?.Schedule is { } scheduleBody)
        {
            if (!TryParseSchedule(scheduleBody, out var parsed, out var error))
            {
                return BadRequest(new { error });
            }

            schedule = parsed;
        }

        return Wrap(() =>
        {
            var existing = _campaigns.Get(id);
            if (existing is null)
            {
                return NotFound();
            }

            if (body?.TemplateId is { Length: > 0 } templateId)
            {
                if (!_templates.Exists(templateId)
                    || _templates.BaseIdOf(templateId) != TemplateStore.DefaultTemplateId(existing.Type))
                {
                    return BadRequest(new { error = "invalid-template" });
                }
            }

            var tz = RecurrenceSchedule.ResolveTimeZone(_config.Get().SchedulerTimeZone);
            var enabling = body?.Enabled == true && !existing.Enabled;
            var scheduleChanged = schedule is not null && !schedule.Equals(existing.Schedule);

            _campaigns.Update(id, c =>
            {
                if (body?.Enabled is { } enabled)
                {
                    c.Enabled = enabled;
                }

                if (body?.MailLanguage is { } mailLanguage)
                {
                    c.MailLanguage = mailLanguage;
                }

                if (body?.TemplateId is { } wantedTemplate)
                {
                    c.TemplateId = wantedTemplate.Length == 0
                        ? TemplateStore.DefaultTemplateId(c.Type)
                        : wantedTemplate;
                }

                if (schedule is not null)
                {
                    c.Schedule = schedule;
                }

                if (scheduleChanged || enabling)
                {
                    c.NextRunUtc = c.Enabled ? c.Schedule.NextRunUtc(DateTime.UtcNow, tz) : c.NextRunUtc;
                }

                if (body?.Enabled == false)
                {
                    c.NextRunUtc = null;
                }
            });

            _easyNotifLog.Info("campaign.updated", new Dictionary<string, object?>
            {
                ["campaignId"] = id,
                ["enabled"] = body?.Enabled,
                ["mailLanguage"] = body?.MailLanguage,
                ["scheduleChanged"] = scheduleChanged
            });
            return NoContent();
        });
    }

    /// <summary>Runs one campaign now, whether or not it is enabled. Administrators only.</summary>
    /// <param name="id">The campaign id.</param>
    /// <returns>200 with the run summary; 404 for an unknown id.</returns>
    [HttpPost("admin/campaigns/{id}/run")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> RunCampaign([FromRoute] string id)
    {
        try
        {
            var result = await _dispatch.RunCampaignNowAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
            return result.Found
                ? Ok(new
                {
                    campaignId = result.CampaignId,
                    sent = result.Sent,
                    failed = result.Failed,
                    skippedNoEmail = result.Skipped,
                    nextRunUtc = result.NextRunUtc
                })
                : NotFound();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] A storage or configuration operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Sends the current content of one campaign to the calling administrator's contact address as a
    /// preview; a weekly recap is filled with the caller's own data (DEC-79). Does not advance the
    /// campaign schedule. Administrators only.
    /// </summary>
    /// <param name="id">The campaign id.</param>
    /// <returns>200 with the digest counts; 400 when the caller has no contact address; 404 for an
    /// unknown id; 503 on a storage failure.</returns>
    [HttpPost("admin/campaigns/{id}/preview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> PreviewCampaign([FromRoute] string id)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);

        string? address;
        try
        {
            address = _preferences.GetOrCreate(userId).ContactEmail;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] A storage or configuration operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return BadRequest(new { error = "no-contact-email" });
        }

        try
        {
            var r = await _dispatch.PreviewAsync(id, userId, address, HttpContext.RequestAborted).ConfigureAwait(false);
            return r.Found
                ? Ok(new { sent = r.Sent, movies = r.Movies, series = r.Series })
                : NotFound();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] A storage or configuration operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    // -------------------------------------------------------------------------
    // Admin - email templates
    // -------------------------------------------------------------------------

    /// <summary>Lists the base and custom email templates. Administrators only.</summary>
    /// <returns>The templates; <c>cloneable</c> is false for <c>series-episodes</c> (DEC-44).</returns>
    [HttpGet("admin/templates")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetTemplates() => Wrap(() => Ok(_templates.List().Select(t => new
    {
        id = t.Id,
        baseId = t.BaseId,
        custom = t.Custom,
        langs = t.Langs,
        cloneable = TemplateStore.CloneableBaseIds.Contains(t.BaseId)
    })));

    /// <summary>Gets one template's source for a language. Administrators only.</summary>
    /// <param name="id">The template id (base or custom).</param>
    /// <param name="lang">The language, <c>en</c> or <c>fr</c> (default <c>en</c>).</param>
    /// <returns>The source; 404 for an unknown id.</returns>
    [HttpGet("admin/templates/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult GetTemplate([FromRoute] string id, [FromQuery] string? lang) => Wrap(() =>
    {
        if (!_templates.Exists(id))
        {
            return NotFound();
        }

        var normalizedLang = lang is "fr" ? "fr" : "en";
        return Ok(new
        {
            id,
            lang = normalizedLang,
            baseId = _templates.BaseIdOf(id),
            custom = !TemplateStore.BaseIds.Contains(id),
            content = _templates.GetRaw(id, normalizedLang)
        });
    });

    /// <summary>Clones a base template to a new custom template. Administrators only.</summary>
    /// <param name="body">The base id and the new slug.</param>
    /// <returns>201; 400 on an invalid base id, slug, or an existing id.</returns>
    [HttpPost("admin/templates")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult CloneTemplate([FromBody] TemplateCloneRequest? body) => Wrap(() =>
    {
        try
        {
            _templates.Clone(body?.BaseId ?? string.Empty, body?.Slug ?? string.Empty);
            _easyNotifLog.Info("template.cloned", new Dictionary<string, object?>
            {
                ["baseId"] = body?.BaseId,
                ["slug"] = body?.Slug
            });
            return StatusCode(StatusCodes.Status201Created, new { id = $"{body?.BaseId}__{body?.Slug}" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = "invalid-clone", detail = ex.Message });
        }
    });

    /// <summary>Saves one language of a custom template. Administrators only.</summary>
    /// <param name="id">The custom template id.</param>
    /// <param name="lang">The language, <c>en</c> or <c>fr</c> (default <c>en</c>).</param>
    /// <param name="body">The template body.</param>
    /// <returns>204; 400 on a failed validation; 403 for a base template; 404 for an unknown id.</returns>
    [HttpPut("admin/templates/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult PutTemplate([FromRoute] string id, [FromQuery] string? lang, [FromBody] TemplateSaveRequest? body) => Wrap(() =>
    {
        if (TemplateStore.BaseIds.Contains(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "base-template-read-only" });
        }

        if (!_templates.Exists(id))
        {
            return NotFound();
        }

        var content = body?.Content ?? string.Empty;
        var validation = _templates.Validate(_templates.BaseIdOf(id), content);
        if (!validation.Ok)
        {
            return BadRequest(new { error = validation.Reason, key = validation.Key });
        }

        _templates.Save(id, lang is "fr" ? "fr" : "en", content);
        _easyNotifLog.Info("template.saved", new Dictionary<string, object?> { ["id"] = id, ["lang"] = lang });
        return NoContent();
    });

    /// <summary>Deletes a custom template. Administrators only.</summary>
    /// <param name="id">The custom template id.</param>
    /// <returns>204; 403 for a base template; 404 when it does not exist; 409 when a campaign still points at it.</returns>
    [HttpDelete("admin/templates/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult DeleteTemplate([FromRoute] string id) => Wrap(() =>
    {
        if (TemplateStore.BaseIds.Contains(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "base-template-read-only" });
        }

        if (!_templates.Exists(id))
        {
            return NotFound(new { error = "unknown-template" });
        }

        var user = _campaigns.All().FirstOrDefault(c => c.TemplateId == id);
        if (user is not null)
        {
            return Conflict(new { error = "template-in-use", campaignId = user.Id });
        }

        _templates.Delete(id);
        _easyNotifLog.Info("template.deleted", new Dictionary<string, object?> { ["id"] = id });
        return NoContent();
    });

    /// <summary>Renders a candidate template body with sample data for the editor preview. Administrators only.</summary>
    /// <param name="body">The base id, language and candidate body.</param>
    /// <returns>200 with the rendered HTML; 400 on an unknown base id or a body that fails validation.</returns>
    [HttpPost("admin/templates/preview")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PreviewTemplate([FromBody] TemplatePreviewRequest? body)
    {
        var baseId = body?.BaseId ?? string.Empty;
        if (!TemplateStore.BaseIds.Contains(baseId))
        {
            return BadRequest(new { error = "invalid-base" });
        }

        var content = body?.Content ?? string.Empty;
        var validation = _templates.Validate(baseId, content);
        if (!validation.Ok)
        {
            return BadRequest(new { error = validation.Reason, key = validation.Key });
        }

        var html = Templating.TemplateEngine.Render(content, Templating.TemplateSampleModel.For(baseId));
        return Ok(new { html });
    }

    // -------------------------------------------------------------------------
    // Static assets (embedded, anonymous)
    // -------------------------------------------------------------------------

    /// <summary>Serves the embedded admin config-page script.</summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("config.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetConfigScript() => Asset("Web.config.js", "application/javascript");

    /// <summary>Serves the embedded user settings panel script (injected into the web client).</summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("enotif-user.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetUserScript() => Asset("Web.enotif-user.js", "application/javascript");

    /// <summary>Serves the embedded user settings panel stylesheet.</summary>
    /// <returns>The CSS file.</returns>
    [HttpGet("enotif-user.css")]
    [AllowAnonymous]
    [Produces("text/css")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetUserStylesheet() => Asset("Web.enotif-user.css", "text/css");

    /// <summary>Serves the embedded UI string bundle for a language.</summary>
    /// <param name="lang">The language: "en" or "fr".</param>
    /// <returns>The JSON string bundle.</returns>
    [HttpGet("strings/{lang}.json")]
    [AllowAnonymous]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetStrings([FromRoute] string lang)
        => lang is "en" or "fr"
            ? Asset($"Web.strings.{lang}.json", "application/json")
            : NotFound();

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ToConfiguredLocal(DateTime? utc, TimeZoneInfo tz)
        => utc is { } value
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), tz)
                .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static bool TryParseSchedule(ScheduleUpdate body, out RecurrenceSchedule schedule, out string? error)
    {
        schedule = RecurrenceSchedule.Daily(default);
        error = null;

        if (!Enum.TryParse<RecurrenceKind>(body.Kind, ignoreCase: true, out var kind))
        {
            error = "invalid-kind";
            return false;
        }

        if (!TimeOnly.TryParseExact(body.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time))
        {
            error = "invalid-time";
            return false;
        }

        switch (kind)
        {
            case RecurrenceKind.Daily:
                schedule = RecurrenceSchedule.Daily(time);
                return true;

            case RecurrenceKind.Weekly:
                if (!Enum.TryParse<DayOfWeek>(body.DayOfWeek, ignoreCase: true, out var dow))
                {
                    error = "invalid-day-of-week";
                    return false;
                }

                schedule = RecurrenceSchedule.Weekly(dow, time);
                return true;

            case RecurrenceKind.Monthly:
                if (body.DayOfMonth is not { } dom || dom is < 1 or > 31)
                {
                    error = "invalid-day-of-month";
                    return false;
                }

                schedule = RecurrenceSchedule.Monthly(dom, time);
                return true;

            case RecurrenceKind.EveryNDays:
                if (body.IntervalDays is not { } interval || interval is < 1 or > RecurrenceSchedule.MaxIntervalDays)
                {
                    error = "invalid-interval-days";
                    return false;
                }

                schedule = RecurrenceSchedule.EveryNDays(interval, time);
                return true;

            default:
                error = "invalid-kind";
                return false;
        }
    }

    private static Dictionary<EmailCategory, bool> BuildCategoryMap(CategoryUpdate? body)
    {
        var map = new Dictionary<EmailCategory, bool>();
        if (body?.News is { } news)
        {
            map[EmailCategory.News] = news;
        }

        if (body?.Recap is { } recap)
        {
            map[EmailCategory.Recap] = recap;
        }

        return map;
    }

    private async Task<Guid> CurrentUserIdAsync()
    {
        var authInfo = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return authInfo.UserId;
    }

    private ActionResult Wrap(Func<ActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] A storage or configuration operation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private IActionResult Asset(string suffix, string contentType)
    {
        var name = PluginAssembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return File(PluginAssembly.GetManifestResourceStream(name)!, contentType);
    }
}

/// <summary>Category opt-in changes. A null field is left unchanged.</summary>
public class CategoryUpdate
{
    /// <summary>Gets or sets the "new media" opt-in state.</summary>
    public bool? News { get; set; }

    /// <summary>Gets or sets the "weekly recap" opt-in state.</summary>
    public bool? Recap { get; set; }

    /// <summary>
    /// Gets or sets whether the caller receives manual announcements. Honoured on
    /// <c>PUT me/preferences</c> only: an admin cannot override a user's opt-out.
    /// </summary>
    public bool? Announcements { get; set; }

    /// <summary>
    /// Gets or sets whether the caller receives new-episode emails of the series they follow.
    /// Honoured on <c>PUT me/preferences</c> only.
    /// </summary>
    public bool? Episodes { get; set; }

    /// <summary>
    /// Gets or sets the newsletter libraries the caller keeps (FEAT-02), as library ids; null leaves
    /// the choice unchanged. Honoured on <c>PUT me/preferences</c> only.
    /// </summary>
    public IReadOnlyList<Guid>? Libraries { get; set; }
}

/// <summary>Admin change to another user's preferences.</summary>
public sealed class AdminPreferenceUpdate : CategoryUpdate
{
    /// <summary>Gets or sets the contact address (null = unchanged, blank = clear).</summary>
    public string? ContactEmail { get; set; }
}

/// <summary>Request body for <c>PUT /EasyNotif/me/contact-email</c>.</summary>
public sealed class ContactEmailUpdate
{
    /// <summary>Gets or sets the contact address, or null/blank to clear it.</summary>
    public string? Email { get; set; }
}

/// <summary>Request body for <c>POST /EasyNotif/me/test</c>.</summary>
public sealed class TestEmailRequest
{
    /// <summary>Gets or sets the UI language for the test message (<c>"fr"</c> or <c>"en"</c>).</summary>
    public string? Lang { get; set; }
}

/// <summary>Request body for <c>POST /EasyNotif/admin/send</c>.</summary>
public sealed class ManualSendRequest
{
    /// <summary>Gets or sets the subject line.</summary>
    public string? Subject { get; set; }

    /// <summary>Gets or sets the HTML body.</summary>
    public string? Html { get; set; }

    /// <summary>Gets or sets the plain-text body (generated from the HTML when absent).</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets the audience: <c>all</c>, <c>selected</c> or <c>test</c>.</summary>
    public string? RecipientMode { get; set; }

    /// <summary>Gets or sets the chosen user ids when the mode is <c>selected</c>.</summary>
    public List<Guid>? RecipientUserIds { get; set; }

    /// <summary>Gets or sets the single address when the mode is <c>test</c>.</summary>
    public string? TestAddress { get; set; }

    /// <summary>Gets or sets the attachments.</summary>
    public List<AttachmentDto>? Attachments { get; set; }

    /// <summary>Gets or sets a value indicating whether the body is laid out with the <c>generic</c> template (absent = raw).</summary>
    public bool? Wrap { get; set; }

    /// <summary>Gets or sets the language of the layout, <c>fr</c> (default) or <c>en</c>.</summary>
    public string? Lang { get; set; }
}

/// <summary>Request body for <c>POST /EasyNotif/admin/announcements</c>.</summary>
public sealed class ScheduleAnnouncementRequest
{
    /// <summary>Gets or sets the subject line.</summary>
    public string? Subject { get; set; }

    /// <summary>Gets or sets the HTML body.</summary>
    public string? Html { get; set; }

    /// <summary>Gets or sets the plain-text body.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets the audience: <c>all</c> (default) or <c>selected</c>.</summary>
    public string? RecipientMode { get; set; }

    /// <summary>Gets or sets the chosen user ids when the mode is <c>selected</c>.</summary>
    public List<Guid>? RecipientUserIds { get; set; }

    /// <summary>Gets or sets a value indicating whether the body is laid out with the <c>generic</c> template.</summary>
    public bool? Wrap { get; set; }

    /// <summary>Gets or sets the language of the layout, <c>fr</c> (default) or <c>en</c>.</summary>
    public string? Lang { get; set; }

    /// <summary>Gets or sets the send time in the scheduler time zone, <c>yyyy-MM-ddTHH:mm</c>.</summary>
    public string? SendAt { get; set; }

    /// <summary>Gets or sets attachments; always refused on a scheduled announcement (DEC-67).</summary>
    public List<AttachmentDto>? Attachments { get; set; }
}

/// <summary>One attachment on a manual email.</summary>
public sealed class AttachmentDto
{
    /// <summary>Gets or sets the file name shown to the recipient.</summary>
    public string? FileName { get; set; }

    /// <summary>Gets or sets the base64-encoded file content.</summary>
    public string? ContentBase64 { get; set; }

    /// <summary>Gets or sets the MIME type, or null.</summary>
    public string? ContentType { get; set; }
}

/// <summary>Request body for <c>PUT /EasyNotif/admin/campaigns/{id}</c>. A null field is left unchanged.</summary>
public sealed class CampaignUpdate
{
    /// <summary>Gets or sets whether the campaign is active.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Gets or sets the mail language (<c>en</c> or <c>fr</c>).</summary>
    public string? MailLanguage { get; set; }

    /// <summary>
    /// Gets or sets the template id used to compose the mail. Null leaves it unchanged; an empty
    /// string resets to the base template for the campaign kind.
    /// </summary>
    public string? TemplateId { get; set; }

    /// <summary>Gets or sets the new recurrence.</summary>
    public ScheduleUpdate? Schedule { get; set; }
}

/// <summary>A recurrence in a campaign update request.</summary>
public sealed class ScheduleUpdate
{
    /// <summary>Gets or sets the kind: <c>daily</c>, <c>weekly</c>, <c>monthly</c> or <c>everyNDays</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the local time of day as <c>HH:mm</c>.</summary>
    public string? Time { get; set; }

    /// <summary>Gets or sets the day of week for a weekly schedule.</summary>
    public string? DayOfWeek { get; set; }

    /// <summary>Gets or sets the 1-31 day of month for a monthly schedule.</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>Gets or sets the day interval for an every-N-days schedule.</summary>
    public int? IntervalDays { get; set; }
}

/// <summary>Request body for <c>POST /EasyNotif/admin/templates</c>.</summary>
public sealed class TemplateCloneRequest
{
    /// <summary>Gets or sets the base template id to clone.</summary>
    public string? BaseId { get; set; }

    /// <summary>Gets or sets the slug for the new custom template.</summary>
    public string? Slug { get; set; }
}

/// <summary>Request body for <c>PUT /EasyNotif/admin/templates/{id}</c>.</summary>
public sealed class TemplateSaveRequest
{
    /// <summary>Gets or sets the template body.</summary>
    public string? Content { get; set; }
}

/// <summary>Request body for <c>POST /EasyNotif/admin/templates/preview</c>.</summary>
public sealed class TemplatePreviewRequest
{
    /// <summary>Gets or sets the base id whose sample data to render with.</summary>
    public string? BaseId { get; set; }

    /// <summary>Gets or sets the language (unused by the sample data; kept for symmetry).</summary>
    public string? Lang { get; set; }

    /// <summary>Gets or sets the candidate template body.</summary>
    public string? Content { get; set; }
}

/// <summary>Request body for <c>PUT /EasyNotif/admin/settings</c>. A blank secret is ignored.</summary>
public sealed class SettingsUpdate
{
    /// <summary>Gets or sets the sender address.</summary>
    public string? FromEmail { get; set; }

    /// <summary>Gets or sets the sender display name.</summary>
    public string? FromName { get; set; }

    /// <summary>Gets or sets the Reply-To address.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>Gets or sets the public base URL of this server.</summary>
    public string? PublicServerUrl { get; set; }

    /// <summary>Gets or sets the IANA scheduler time zone.</summary>
    public string? SchedulerTimeZone { get; set; }

    /// <summary>Gets or sets the newsletter movie cap (1 to 200). Null leaves it unchanged.</summary>
    public int? NewsletterMaxMovies { get; set; }

    /// <summary>Gets or sets the newsletter series cap (1 to 200). Null leaves it unchanged.</summary>
    public int? NewsletterMaxSeries { get; set; }

    /// <summary>Gets or sets the daily send limit (0 to 100000, 0 = no daily check). Null leaves it unchanged.</summary>
    public int? QuotaDailyLimit { get; set; }

    /// <summary>Gets or sets the 30-day send limit (1 to 1000000). Null leaves it unchanged.</summary>
    public int? QuotaMonthlyLimit { get; set; }

    /// <summary>Gets or sets whether the web UI is injected (null = unchanged; takes effect after a restart).</summary>
    public bool? InjectWebUi { get; set; }

    /// <summary>Gets or sets whether the series bell is shown (null = unchanged; takes effect after a restart).</summary>
    public bool? InjectSeriesBell { get; set; }

    /// <summary>Gets or sets the incident digest address. Null leaves it unchanged; blank turns the digest off.</summary>
    public string? AlertEmail { get; set; }

    /// <summary>Gets or sets the incident digest language, <c>"fr"</c> or <c>"en"</c>. Null leaves it unchanged.</summary>
    public string? AlertLanguage { get; set; }

    /// <summary>Gets or sets a new Resend API key. Blank leaves the stored one untouched.</summary>
    public string? ResendApiKey { get; set; }

    /// <summary>Gets or sets a new webhook signing secret. Blank leaves the stored one untouched.</summary>
    public string? WebhookSigningSecret { get; set; }
}
