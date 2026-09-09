using Jellyfin.Plugin.EasyNotif.Configuration;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="ISecretStore"/> for tests. Holds the three secrets and counts writes; the
/// real store needs a file system and the plugin data directory.
/// </summary>
public sealed class FakeSecretStore : ISecretStore
{
    /// <summary>Initializes a new instance of the <see cref="FakeSecretStore"/> class.</summary>
    /// <param name="resendApiKey">The starting Resend API key.</param>
    /// <param name="webhookSigningSecret">The starting webhook signing secret.</param>
    /// <param name="unsubscribeSecret">The starting unsubscribe secret.</param>
    public FakeSecretStore(
        string? resendApiKey = null,
        string? webhookSigningSecret = null,
        string? unsubscribeSecret = null)
    {
        ResendApiKey = resendApiKey;
        WebhookSigningSecret = webhookSigningSecret;
        UnsubscribeSecret = unsubscribeSecret;
    }

    /// <summary>Gets or sets the stored Resend API key.</summary>
    public string? ResendApiKey { get; set; }

    /// <summary>Gets or sets the stored webhook signing secret.</summary>
    public string? WebhookSigningSecret { get; set; }

    /// <summary>Gets or sets the stored unsubscribe secret.</summary>
    public string? UnsubscribeSecret { get; set; }

    /// <inheritdoc/>
    public SecretsView Get() => new(ResendApiKey, WebhookSigningSecret, UnsubscribeSecret);

    /// <inheritdoc/>
    public void SetResendApiKey(string? value) => ResendApiKey = value;

    /// <inheritdoc/>
    public void SetWebhookSigningSecret(string? value) => WebhookSigningSecret = value;

    /// <inheritdoc/>
    public bool EnsureUnsubscribeSecret()
    {
        if (!string.IsNullOrEmpty(UnsubscribeSecret))
        {
            return false;
        }

        UnsubscribeSecret = "test-unsubscribe-secret-0123456789";
        return true;
    }

    /// <inheritdoc/>
    public bool MigrateFrom(PluginConfiguration legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var changed = false;

        if (string.IsNullOrEmpty(ResendApiKey) && !string.IsNullOrEmpty(legacy.ResendApiKey))
        {
            ResendApiKey = legacy.ResendApiKey;
        }

        if (string.IsNullOrEmpty(WebhookSigningSecret) && !string.IsNullOrEmpty(legacy.WebhookSigningSecret))
        {
            WebhookSigningSecret = legacy.WebhookSigningSecret;
        }

        if (string.IsNullOrEmpty(UnsubscribeSecret) && !string.IsNullOrEmpty(legacy.UnsubscribeSecret))
        {
            UnsubscribeSecret = legacy.UnsubscribeSecret;
        }

        if (legacy.ResendApiKey is not null || legacy.WebhookSigningSecret is not null || legacy.UnsubscribeSecret is not null)
        {
            legacy.ResendApiKey = null;
            legacy.WebhookSigningSecret = null;
            legacy.UnsubscribeSecret = null;
            changed = true;
        }

        return changed;
    }
}
