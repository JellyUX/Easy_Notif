using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Unsubscribe;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Controllers;

/// <summary>
/// The public one-click unsubscribe endpoint <c>GET|POST /EasyNotif/u/{token}</c> (Synthese.md
/// section 7.2). Anonymous but token-gated: an invalid token gets 404. <c>GET</c> renders a small
/// confirmation page and never changes anything; <c>POST</c> toggles the preference (RFC 8058
/// one-click, or the page's form).
/// </summary>
[ApiController]
[Route("EasyNotif")]
public class UnsubscribeController : ControllerBase
{
    private readonly IConfigAccessor _config;
    private readonly IPreferenceService _preferences;
    private readonly IEasyNotifLog _easyNotifLog;
    private readonly ILogger<UnsubscribeController> _logger;

    /// <summary>Initializes a new instance of the <see cref="UnsubscribeController"/> class.</summary>
    /// <param name="config">The plugin configuration accessor (for the unsubscribe secret).</param>
    /// <param name="preferences">The preference service.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    /// <param name="logger">Logger.</param>
    public UnsubscribeController(
        IConfigAccessor config,
        IPreferenceService preferences,
        IEasyNotifLog easyNotifLog,
        ILogger<UnsubscribeController> logger)
    {
        _config = config;
        _preferences = preferences;
        _easyNotifLog = easyNotifLog;
        _logger = logger;
    }

    /// <summary>Renders the confirmation page for a token. Does not change anything.</summary>
    /// <param name="token">The signed unsubscribe token.</param>
    /// <returns>The HTML page, 404 for an invalid token, or 503 when storage is unavailable.</returns>
    [HttpGet("u/{token}")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Get([FromRoute] string token)
    {
        if (!Resolve(token, out var userId, out var category))
        {
            return NotFound();
        }

        return Guarded(() => Page(userId, category, done: false));
    }

    /// <summary>Toggles the preference the token targets. One-click bodies get a bare 200.</summary>
    /// <param name="token">The signed unsubscribe token.</param>
    /// <returns>200 (bare for one-click, otherwise the page), or 404 for an invalid token.</returns>
    [HttpPost("u/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Post([FromRoute] string token)
    {
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
            _preferences.SetCategories(userId, CategoryMap(category, target));

            _easyNotifLog.Info("unsubscribe.action", new Dictionary<string, object?>
            {
                ["userId"] = userId,
                ["category"] = category,
                ["resubscribe"] = resubscribe,
                ["oneClick"] = oneClick
            });

            return oneClick ? Ok() : Page(userId, category, done: true);
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

        var secret = _config.Get().UnsubscribeSecret;
        if (!UnsubscribeToken.TryVerify(secret ?? string.Empty, token, out userId, out category))
        {
            return false;
        }

        return category is "news" or "recap" or "all";
    }

    private ContentResult Page(Guid userId, string category, bool done)
    {
        var subscribed = IsSubscribed(userId, category);
        var lang = PickLang();
        return Content(UnsubscribePage.Build(lang, category, subscribed, done), "text/html; charset=utf-8");
    }

    private bool IsSubscribed(Guid userId, string category)
    {
        var pref = _preferences.GetOrCreate(userId);
        return category switch
        {
            "news" => pref.Categories.GetValueOrDefault(EmailCategory.News),
            "recap" => pref.Categories.GetValueOrDefault(EmailCategory.Recap),
            _ => pref.Categories.GetValueOrDefault(EmailCategory.News)
                 || pref.Categories.GetValueOrDefault(EmailCategory.Recap),
        };
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
