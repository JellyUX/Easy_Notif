using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EasyNotif.Configuration;

/// <summary>
/// Plugin configuration. Holds the transport secrets and the small set of URLs the plugin needs.
/// The real business data (preferences, campaigns, playback history, templates, logs) lives in one
/// deletable directory under DataPath, not in this XML. See CLAUDE.md and Synthese.md section 11.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a startup warning surfaced on the configuration page (for example, when the
    /// FileTransformation plugin is missing). Null when there is nothing to report.
    /// </summary>
    public string? StartupWarning { get; set; }

    /// <summary>
    /// Gets or sets the Resend API key (secret). Masked on read, never logged. Format: "re_...".
    /// </summary>
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
    /// Gets or sets the Resend webhook signing secret (secret). Format: "whsec_...".
    /// </summary>
    public string? WebhookSigningSecret { get; set; }

    /// <summary>
    /// Gets or sets the HMAC secret used to sign one-click unsubscribe tokens. Generated once on
    /// first start when empty.
    /// </summary>
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
}
