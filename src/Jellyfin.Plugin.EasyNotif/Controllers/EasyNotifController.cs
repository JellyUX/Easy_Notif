using System.Net.Mail;
using System.Reflection;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
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
[ApiController]
[Route("EasyNotif")]
public class EasyNotifController : ControllerBase
{
    private static readonly Assembly PluginAssembly = typeof(EasyNotifController).Assembly;

    private readonly IPreferenceService _preferences;
    private readonly IConfigAccessor _config;
    private readonly IAuthorizationContext _authContext;
    private readonly IEmailSender _emailSender;
    private readonly IQuotaGuard _quota;
    private readonly ISendLog _sendLog;
    private readonly ILogger<EasyNotifController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EasyNotifController"/> class.
    /// </summary>
    /// <param name="preferences">The preference service.</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="authContext">Jellyfin request authorization context.</param>
    /// <param name="emailSender">The email transport.</param>
    /// <param name="quota">The send quota guard.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="logger">Logger.</param>
    public EasyNotifController(
        IPreferenceService preferences,
        IConfigAccessor config,
        IAuthorizationContext authContext,
        IEmailSender emailSender,
        IQuotaGuard quota,
        ISendLog sendLog,
        ILogger<EasyNotifController> logger)
    {
        _preferences = preferences;
        _config = config;
        _authContext = authContext;
        _emailSender = emailSender;
        _quota = quota;
        _sendLog = sendLog;
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
            return Ok(new
            {
                news = pref.Categories.GetValueOrDefault(EmailCategory.News),
                recap = pref.Categories.GetValueOrDefault(EmailCategory.Recap)
            });
        });
    }

    /// <summary>Sets the caller's category opt-in state. Omitted categories are left unchanged.</summary>
    /// <param name="body">The categories to change.</param>
    /// <returns>204 on success; 503 when storage is unavailable.</returns>
    [HttpPut("me/preferences")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> PutMyPreferences([FromBody] CategoryUpdate body)
    {
        var userId = await CurrentUserIdAsync().ConfigureAwait(false);
        return Wrap(() =>
        {
            _preferences.SetCategories(userId, BuildCategoryMap(body));
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
    /// <returns>200 with <c>{ ok }</c>; 400 when the caller has no contact address; 503 when storage
    /// is unavailable.</returns>
    [HttpPost("me/test")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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

        var lang = string.Equals(body?.Lang, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
        var (subject, html) = TestEmail.Build(lang);

        var result = await _emailSender.SendAsync(
            new EmailMessage
            {
                To = address,
                Subject = subject,
                Html = html,
                ReplyTo = _config.Get().ReplyTo
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
        return Ok(new
        {
            fromEmail = cfg.FromEmail,
            fromName = cfg.FromName,
            replyTo = cfg.ReplyTo,
            publicServerUrl = cfg.PublicServerUrl,
            schedulerTimeZone = cfg.SchedulerTimeZone,
            resendApiKeySet = !string.IsNullOrEmpty(cfg.ResendApiKey),
            webhookSigningSecretSet = !string.IsNullOrEmpty(cfg.WebhookSigningSecret),
            startupWarning = cfg.StartupWarning
        });
    });

    /// <summary>
    /// Updates the plugin settings. A blank secret leaves the stored one untouched. Administrators only.
    /// </summary>
    /// <param name="body">The settings.</param>
    /// <returns>204 on success; 400 on an invalid time zone or address; 503 when the plugin instance
    /// is unavailable.</returns>
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

        foreach (var address in new[] { body.FromEmail, body.ReplyTo })
        {
            if (!string.IsNullOrWhiteSpace(address) && !MailAddress.TryCreate(address.Trim(), out _))
            {
                return BadRequest();
            }
        }

        return Wrap(() =>
        {
            var cfg = _config.Get();
            cfg.FromEmail = Trimmed(body.FromEmail);
            cfg.FromName = Trimmed(body.FromName);
            cfg.ReplyTo = Trimmed(body.ReplyTo);
            cfg.PublicServerUrl = Trimmed(body.PublicServerUrl);
            if (!string.IsNullOrWhiteSpace(body.SchedulerTimeZone))
            {
                cfg.SchedulerTimeZone = body.SchedulerTimeZone.Trim();
            }

            if (!string.IsNullOrWhiteSpace(body.ResendApiKey))
            {
                cfg.ResendApiKey = body.ResendApiKey.Trim();
            }

            if (!string.IsNullOrWhiteSpace(body.WebhookSigningSecret))
            {
                cfg.WebhookSigningSecret = body.WebhookSigningSecret.Trim();
            }

            _config.Save();
            return NoContent();
        });
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

    /// <summary>Gets or sets a new Resend API key. Blank leaves the stored one untouched.</summary>
    public string? ResendApiKey { get; set; }

    /// <summary>Gets or sets a new webhook signing secret. Blank leaves the stored one untouched.</summary>
    public string? WebhookSigningSecret { get; set; }
}
