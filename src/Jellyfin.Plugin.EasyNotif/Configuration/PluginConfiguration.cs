using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EasyNotif.Configuration;

/// <summary>
/// Plugin configuration. Holds the small set of URLs the plugin needs. The transport secrets live in
/// <see cref="ISecretStore"/> (a separate <c>secrets.json</c>), not here, so they are never returned
/// by Jellyfin's stock <c>GET /Plugins/{guid}/Configuration</c> endpoint. The real business data
/// (preferences, campaigns, playback history, templates, logs) lives in one deletable directory
/// under DataPath, not in this XML. See CLAUDE.md and Synthese.md section 11.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a startup warning surfaced on the configuration page (for example, when the
    /// FileTransformation plugin is missing). Null when there is nothing to report.
    /// </summary>
    public string? StartupWarning { get; set; }

    /// <summary>
    /// Gets or sets the Resend API key. Kept only as the one-time migration source into
    /// <see cref="ISecretStore"/>; blanked after migration. Not serialized to JSON.
    /// </summary>
    [JsonIgnore]
    public string? ResendApiKey { get; set; }

    /// <summary>
    /// Gets or sets the sender address, on the verified Resend sending domain.
    /// </summary>
    public string? FromEmail { get; set; }

    /// <summary>
    /// Gets or sets the sender display name shown to recipients.
    /// </summary>
    public string? FromName { get; set; }

    /// <summary>
    /// Gets or sets an optional Reply-To address. A recipient reply goes straight to this address;
    /// no inbound mail handling is required.
    /// </summary>
    public string? ReplyTo { get; set; }

    /// <summary>
    /// Gets or sets the Resend webhook signing secret. Kept only as the one-time migration source
    /// into <see cref="ISecretStore"/>; blanked after migration. Not serialized to JSON.
    /// </summary>
    [JsonIgnore]
    public string? WebhookSigningSecret { get; set; }

    /// <summary>
    /// Gets or sets the HMAC secret used to sign one-click unsubscribe tokens. Kept only as the
    /// one-time migration source into <see cref="ISecretStore"/>; blanked after migration. Not
    /// serialized to JSON.
    /// </summary>
    [JsonIgnore]
    public string? UnsubscribeSecret { get; set; }

    /// <summary>
    /// Gets or sets the public base URL of this Jellyfin server (for example
    /// "https://media.example.com"), used to build poster image URLs and deep links in emails.
    /// When empty, the newsletter falls back to inline CID attachments and omits deep links.
    /// </summary>
    public string? PublicServerUrl { get; set; }

    /// <summary>
    /// Gets or sets the IANA time zone used to evaluate campaign schedules. Defaults to
    /// "Europe/Paris".
    /// </summary>
    public string SchedulerTimeZone { get; set; } = "Europe/Paris";

    /// <summary>
    /// Gets or sets how many movies a newsletter lists before "and N more" (1 to 200, default 30).
    /// </summary>
    public int NewsletterMaxMovies { get; set; } = DefaultNewsletterCap;

    /// <summary>
    /// Gets or sets how many series a newsletter lists before "and N more" (1 to 200, default 30).
    /// </summary>
    public int NewsletterMaxSeries { get; set; } = DefaultNewsletterCap;

    /// <summary>The default newsletter cap, per section.</summary>
    public const int DefaultNewsletterCap = 30;

    /// <summary>The highest accepted newsletter cap, per section.</summary>
    public const int MaxNewsletterCap = 200;

    /// <summary>
    /// Gets or sets the provider's daily send limit (0 to 100000, default 100). 0 disables the daily
    /// check and the campaign reserve, for a paid plan without a daily quota.
    /// </summary>
    public int QuotaDailyLimit { get; set; } = DefaultQuotaDailyLimit;

    /// <summary>
    /// Gets or sets the provider's send limit over a rolling 30 days (1 to 1000000, default 3000).
    /// </summary>
    public int QuotaMonthlyLimit { get; set; } = DefaultQuotaMonthlyLimit;

    /// <summary>The default daily send limit (Resend free tier).</summary>
    public const int DefaultQuotaDailyLimit = 100;

    /// <summary>The highest accepted daily send limit.</summary>
    public const int MaxQuotaDailyLimit = 100_000;

    /// <summary>The default 30-day send limit (Resend free tier).</summary>
    public const int DefaultQuotaMonthlyLimit = 3000;

    /// <summary>The highest accepted 30-day send limit.</summary>
    public const int MaxQuotaMonthlyLimit = 1_000_000;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin injects its settings panel (and the series
    /// bell) into Jellyfin Web. Read at startup: a change takes effect after a restart. Turning it off
    /// is the way out when a Jellyfin Web update breaks the injection.
    /// </summary>
    public bool InjectWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the injected script shows the follow bell on series
    /// pages. Ignored when <see cref="InjectWebUi"/> is off; takes effect after a restart.
    /// </summary>
    public bool InjectSeriesBell { get; set; } = true;

    /// <summary>
    /// Gets or sets the address that receives the daily incident digest (FEAT-04), or null to send
    /// none. The admin page banner shows the incidents either way.
    /// </summary>
    public string? AlertEmail { get; set; }

    /// <summary>Gets or sets the language of the incident digest, <c>"fr"</c> or <c>"en"</c>.</summary>
    public string AlertLanguage { get; set; } = "fr";
}
