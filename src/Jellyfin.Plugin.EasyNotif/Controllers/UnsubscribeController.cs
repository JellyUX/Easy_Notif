using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Unsubscribe;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Controllers;

/// <summary>
/// The public one-click unsubscribe endpoint <c>GET|POST /EasyNotif/u/{token}</c> (Synthese.md
/// section 7.2). Anonymous but token-gated: an invalid or expired token gets 404, and each client
/// address is rate-limited (429). <c>GET</c> renders a neutral confirmation page that neither
/// changes nor reveals the current preference; <c>POST</c> applies it (RFC 8058 one-click, or the
/// page's form).
/// </summary>
[ApiController]
[Route("EasyNotif")]
public class UnsubscribeController : ControllerBase
{
    /// <summary>The Content-Security-Policy sent with the confirmation page (audit SEC-08).</summary>
    public const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";

    private readonly ISecretStore _secrets;
    private readonly IPreferenceService _preferences;
    private readonly IUserManager _userManager;
    private readonly UnsubscribeRateLimiter _rateLimiter;
    private readonly TimeProvider _time;
    private readonly IEasyNotifLog _easyNotifLog;
    private readonly ILogger<UnsubscribeController> _logger;

    /// <summary>Initializes a new instance of the <see cref="UnsubscribeController"/> class.</summary>
    /// <param name="secrets">The transport secret store (for the unsubscribe secret).</param>
    /// <param name="preferences">The preference service.</param>
    /// <param name="userManager">Jellyfin user manager, to reject tokens of deleted accounts.</param>
    /// <param name="rateLimiter">The per-address request limiter.</param>
    /// <param name="time">The clock, for token expiry.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    /// <param name="logger">Logger.</param>
    public UnsubscribeController(
        ISecretStore secrets,
        IPreferenceService preferences,
        IUserManager userManager,
        UnsubscribeRateLimiter rateLimiter,
        TimeProvider time,
        IEasyNotifLog easyNotifLog,
        ILogger<UnsubscribeController> logger)
    {
        _secrets = secrets;
        _preferences = preferences;
        _userManager = userManager;
        _rateLimiter = rateLimiter;
        _time = time;
        _easyNotifLog = easyNotifLog;
        _logger = logger;
    }

    /// <summary>Renders the confirmation page for a token. Does not change or show anything stored.</summary>
    /// <param name="token">The signed unsubscribe token.</param>
    /// <returns>The HTML page, 404 for an invalid token, or 429 over the rate limit.</returns>
    [HttpGet("u/{token}")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult Get([FromRoute] string token)
    {
        if (!_rateLimiter.TryAcquire(HttpContext.Connection.RemoteIpAddress))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!Resolve(token, out _, out var category))
        {
            return NotFound();
        }

        return Page(category, done: false, resubscribed: false);
    }

    /// <summary>Applies the preference the token targets. One-click bodies get a bare 200.</summary>
    /// <param name="token">The signed unsubscribe token.</param>
    /// <returns>200 (bare for one-click, otherwise the page), 404 for an invalid token, or 429 over the rate limit.</returns>
    [HttpPost("u/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Post([FromRoute] string token)
    {
        if (!_rateLimiter.TryAcquire(HttpContext.Connection.RemoteIpAddress))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (!Resolve(token, out var userId, out var category))
        {
            return NotFound();
        }

        var form = Request.HasFormContentType ? Request.Form : null;
        var oneClick = form is not null
            && form.TryGetValue("List-Unsubscribe", out var listValue)
            && string.Equals(listValue.ToString(), "One-Click", StringComparison.OrdinalIgnoreCase);
        var resubscribe = form is not null
            && form.TryGetValue("resubscribe", out var resubValue)
            && string.Equals(resubValue.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        // No field (a one-click POST) means unsubscribe; the page's form sends resubscribe=true to opt back in.
        var target = resubscribe;

        return Guarded(() =>
        {
            // "all" also covers manual announcements, which are opt-out: unsubscribing sets the opt-out.
            _preferences.SetCategories(userId, CategoryMap(category, target), category == "all" ? !target : null);

            _easyNotifLog.Info("unsubscribe.action", new Dictionary<string, object?>
            {
                ["userId"] = userId,
                ["category"] = category,
                ["resubscribe"] = resubscribe,
                ["oneClick"] = oneClick
            });

            return oneClick ? Ok() : Page(category, done: true, resubscribed: resubscribe);
        });
    }

    // Maps a storage or configuration failure to a bare 503, like EasyNotifController.Wrap.
    private IActionResult Guarded(Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(ex, "[EasyNotif] Could not apply an unsubscribe action.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private bool Resolve(string token, out Guid userId, out string category)
    {
        userId = Guid.Empty;
        category = string.Empty;

        var secret = _secrets.Get().UnsubscribeSecret;
        if (!UnsubscribeToken.TryVerify(secret ?? string.Empty, token, _time.GetUtcNow(), out userId, out category))
        {
            return false;
        }

        // A deleted account's token must not recreate a preferences row after the purge.
        return category is "news" or "recap" or "all"
            && _userManager.GetUserById(userId) is not null;
    }

    private ContentResult Page(string category, bool done, bool resubscribed)
    {
        // Inline styles only, a same-origin form, never framed (clickjacking a resubscribe), and the
        // token-bearing URL never leaks as a referrer.
        Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return Content(UnsubscribePage.Build(PickLang(), category, done, resubscribed), "text/html; charset=utf-8");
    }

    private static Dictionary<EmailCategory, bool> CategoryMap(string category, bool value) => category switch
    {
        "news" => new() { [EmailCategory.News] = value },
        "recap" => new() { [EmailCategory.Recap] = value },
        _ => new() { [EmailCategory.News] = value, [EmailCategory.Recap] = value },
    };

    private string PickLang()
    {
        if (string.Equals(Request.Query["lang"], "fr", StringComparison.OrdinalIgnoreCase))
        {
            return "fr";
        }

        if (string.Equals(Request.Query["lang"], "en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        var accept = Request.Headers.AcceptLanguage.ToString();
        return accept.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
    }
}
