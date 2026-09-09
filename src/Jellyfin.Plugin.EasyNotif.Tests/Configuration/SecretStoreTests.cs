using System.Text.RegularExpressions;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Configuration;

/// <summary>
/// Covers <see cref="SecretStore"/>: round-tripping the three secrets, generating the unsubscribe
/// secret exactly once, and the one-time migration bridge that moves secrets off the legacy plugin
/// configuration and blanks them.
/// </summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "enotif-secretstore-tests-" + Guid.NewGuid());

    private SecretStore Build()
    {
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.Setup(p => p.DataPath).Returns(_tempDir);
        return new SecretStore(applicationPaths.Object, new FileSystem(), NullLogger<SecretStore>.Instance);
    }

    [Fact]
    public void SetAndGet_RoundTripTheThreeSecrets_AndSurviveAReload()
    {
        var store = Build();
        store.SetResendApiKey("re_key");
        store.SetWebhookSigningSecret("whsec_key");
        store.EnsureUnsubscribeSecret();

        var reloaded = Build().Get();

        Assert.Equal("re_key", reloaded.ResendApiKey);
        Assert.Equal("whsec_key", reloaded.WebhookSigningSecret);
        Assert.False(string.IsNullOrEmpty(reloaded.UnsubscribeSecret));
    }

    [Fact]
    public void EnsureUnsubscribeSecret_GeneratesABase64UrlValueOnce()
    {
        var store = Build();

        Assert.True(store.EnsureUnsubscribeSecret());
        var first = store.Get().UnsubscribeSecret!;
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), first);
        Assert.True(first.Length >= 40, "a 32-byte secret is about 43 base64url chars");

        Assert.False(store.EnsureUnsubscribeSecret());
        Assert.Equal(first, store.Get().UnsubscribeSecret);
    }

    [Fact]
    public void MigrateFrom_MovesEachSecretIntoAnEmptySlot_ThenBlanksTheLegacyConfig()
    {
        var store = Build();
        var legacy = new PluginConfiguration
        {
            ResendApiKey = "re_legacy",
            WebhookSigningSecret = "whsec_legacy",
            UnsubscribeSecret = "unsub_legacy"
        };

        Assert.True(store.MigrateFrom(legacy));

        Assert.Equal("re_legacy", store.Get().ResendApiKey);
        Assert.Equal("whsec_legacy", store.Get().WebhookSigningSecret);
        Assert.Equal("unsub_legacy", store.Get().UnsubscribeSecret);
        Assert.Null(legacy.ResendApiKey);
        Assert.Null(legacy.WebhookSigningSecret);
        Assert.Null(legacy.UnsubscribeSecret);
    }

    [Fact]
    public void MigrateFrom_DoesNotOverwriteAnAlreadyStoredSecret()
    {
        var store = Build();
        store.SetResendApiKey("re_current");

        store.MigrateFrom(new PluginConfiguration { ResendApiKey = "re_legacy" });

        Assert.Equal("re_current", store.Get().ResendApiKey);
    }

    [Fact]
    public void MigrateFrom_WhenNothingToMigrate_ReturnsFalse()
    {
        var store = Build();

        Assert.False(store.MigrateFrom(new PluginConfiguration()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
