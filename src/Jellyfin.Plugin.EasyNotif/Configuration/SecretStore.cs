using System.Security.Cryptography;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Configuration;

/// <summary>On-disk shape of <c>secrets.json</c>.</summary>
public sealed class SecretsFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets or sets the Resend API key.</summary>
    public string? ResendApiKey { get; set; }

    /// <summary>Gets or sets the Resend webhook signing secret.</summary>
    public string? WebhookSigningSecret { get; set; }

    /// <summary>Gets or sets the HMAC secret for one-click unsubscribe tokens.</summary>
    public string? UnsubscribeSecret { get; set; }
}

/// <inheritdoc cref="ISecretStore"/>
public sealed class SecretStore : JsonFileStore<SecretsFile>, ISecretStore
{
    /// <summary>Initializes a new instance of the <see cref="SecretStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public SecretStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<SecretStore> logger)
        : base(applicationPaths, fileSystem, logger, "secrets.json")
    {
    }

    /// <inheritdoc/>
    public SecretsView Get()
        => Read(f => new SecretsView(f.ResendApiKey, f.WebhookSigningSecret, f.UnsubscribeSecret));

    /// <inheritdoc/>
    public void SetResendApiKey(string? value)
        => Mutate(f =>
        {
            if (f.ResendApiKey == value)
            {
                return false;
            }

            f.ResendApiKey = value;
            return true;
        });

    /// <inheritdoc/>
    public void SetWebhookSigningSecret(string? value)
        => Mutate(f =>
        {
            if (f.WebhookSigningSecret == value)
            {
                return false;
            }

            f.WebhookSigningSecret = value;
            return true;
        });

    /// <inheritdoc/>
    public bool EnsureUnsubscribeSecret()
    {
        var generated = false;
        Mutate(f =>
        {
            if (!string.IsNullOrEmpty(f.UnsubscribeSecret))
            {
                return false;
            }

            f.UnsubscribeSecret = GenerateBase64Url(32);
            generated = true;
            return true;
        });
        return generated;
    }

    /// <inheritdoc/>
    public bool MigrateFrom(PluginConfiguration legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);

        Mutate(f =>
        {
            var changed = false;
            if (string.IsNullOrEmpty(f.ResendApiKey) && !string.IsNullOrEmpty(legacy.ResendApiKey))
            {
                f.ResendApiKey = legacy.ResendApiKey;
                changed = true;
            }

            if (string.IsNullOrEmpty(f.WebhookSigningSecret) && !string.IsNullOrEmpty(legacy.WebhookSigningSecret))
            {
                f.WebhookSigningSecret = legacy.WebhookSigningSecret;
                changed = true;
            }

            if (string.IsNullOrEmpty(f.UnsubscribeSecret) && !string.IsNullOrEmpty(legacy.UnsubscribeSecret))
            {
                f.UnsubscribeSecret = legacy.UnsubscribeSecret;
                changed = true;
            }

            return changed;
        });

        if (legacy.ResendApiKey is null && legacy.WebhookSigningSecret is null && legacy.UnsubscribeSecret is null)
        {
            return false;
        }

        legacy.ResendApiKey = null;
        legacy.WebhookSigningSecret = null;
        legacy.UnsubscribeSecret = null;
        return true;
    }

    /// <inheritdoc/>
    protected override void OnWritten(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "[EasyNotif] Could not restrict permissions on {File}.", Path.GetFileName(filePath));
        }
    }

    private static string GenerateBase64Url(int byteCount)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
