namespace Jellyfin.Plugin.EasyNotif.Configuration;

/// <summary>An immutable snapshot of the three transport secrets.</summary>
/// <param name="ResendApiKey">The Resend API key, or null.</param>
/// <param name="WebhookSigningSecret">The Resend webhook signing secret, or null.</param>
/// <param name="UnsubscribeSecret">The HMAC secret for one-click unsubscribe tokens, or null.</param>
public sealed record SecretsView(string? ResendApiKey, string? WebhookSigningSecret, string? UnsubscribeSecret);

/// <summary>
/// Holds the plugin's transport secrets in a dedicated <c>secrets.json</c> under the plugin data
/// directory, out of the XML plugin configuration that Jellyfin serves through its stock
/// <c>GET /Plugins/{guid}/Configuration</c> endpoint. See <see cref="SecretStore"/>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns a snapshot of the stored secrets.</summary>
    /// <returns>The secrets.</returns>
    SecretsView Get();

    /// <summary>Stores (or clears, when null) the Resend API key.</summary>
    /// <param name="value">The new value, or null to clear it.</param>
    void SetResendApiKey(string? value);

    /// <summary>Stores (or clears, when null) the Resend webhook signing secret.</summary>
    /// <param name="value">The new value, or null to clear it.</param>
    void SetWebhookSigningSecret(string? value);

    /// <summary>
    /// Generates a fresh 32-byte base64url unsubscribe secret when none is stored yet.
    /// </summary>
    /// <returns>True when a secret was generated (the first run), false when one already existed.</returns>
    bool EnsureUnsubscribeSecret();

    /// <summary>
    /// One-time upgrade bridge: copies any secret still living on the legacy plugin configuration
    /// into the store (only into an empty slot), then blanks those legacy properties so nothing
    /// remains in the serialized XML.
    /// </summary>
    /// <param name="legacy">The plugin configuration loaded from disk.</param>
    /// <returns>True when <paramref name="legacy"/> was changed and the caller must persist it.</returns>
    bool MigrateFrom(PluginConfiguration legacy);
}
