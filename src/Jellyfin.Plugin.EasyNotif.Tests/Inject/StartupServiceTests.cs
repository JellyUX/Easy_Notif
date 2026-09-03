using System.Text.RegularExpressions;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Inject;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Inject;

/// <summary>
/// Covers <see cref="StartupService"/>: the unsubscribe secret is generated exactly once, the
/// index.html transformation is registered only when FileTransformation is available, and its
/// absence sets a startup warning instead of throwing (R10).
/// </summary>
public sealed class StartupServiceTests
{
    private static StartupService Build(Mock<IFileTransformationDetector> detector, FakeConfigAccessor config)
        => new(NullLogger<StartupService>.Instance, detector.Object, config);

    private static Mock<IFileTransformationDetector> Detector(bool available)
    {
        var mock = new Mock<IFileTransformationDetector>();
        mock.Setup(d => d.IsAvailable()).Returns(available);
        return mock;
    }

    [Fact]
    public async Task StartAsync_WhenDetectorUnavailable_SetsStartupWarning_DoesNotRegister()
    {
        var detector = Detector(available: false);
        var config = new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "set" });

        await Build(detector, config).StartAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(config.Config.StartupWarning));
        detector.Verify(d => d.RegisterTransformation(It.IsAny<JObject>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_WhenDetectorAvailable_RegistersIndexHtml_ClearsWarning()
    {
        var detector = Detector(available: true);
        var config = new FakeConfigAccessor(new PluginConfiguration
        {
            UnsubscribeSecret = "set",
            StartupWarning = "old warning"
        });

        await Build(detector, config).StartAsync(CancellationToken.None);

        Assert.Null(config.Config.StartupWarning);
        detector.Verify(
            d => d.RegisterTransformation(It.Is<JObject>(p =>
                (string?)p["fileNamePattern"] == "index.html"
                && (string?)p["callbackMethod"] == "IndexHtml")),
            Times.Once);
    }

    [Fact]
    public void EnsureUnsubscribeSecret_WhenEmpty_GeneratesABase64UrlValue()
    {
        var cfg = new PluginConfiguration();

        var generated = StartupService.EnsureUnsubscribeSecret(cfg);

        Assert.True(generated);
        Assert.False(string.IsNullOrEmpty(cfg.UnsubscribeSecret));
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), cfg.UnsubscribeSecret!);
        Assert.True(cfg.UnsubscribeSecret!.Length >= 40, "a 32-byte secret is about 43 base64url chars");
    }

    [Fact]
    public void EnsureUnsubscribeSecret_WhenAlreadySet_ReturnsFalse_LeavesItUnchanged()
    {
        var cfg = new PluginConfiguration { UnsubscribeSecret = "existing-value" };

        Assert.False(StartupService.EnsureUnsubscribeSecret(cfg));
        Assert.Equal("existing-value", cfg.UnsubscribeSecret);
    }

    [Fact]
    public async Task StartAsync_WhenSecretMissing_GeneratesItAndPersists()
    {
        var config = new FakeConfigAccessor();

        await Build(Detector(available: true), config).StartAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(config.Config.UnsubscribeSecret));
        Assert.True(config.SaveCount >= 1);
    }

    [Fact]
    public async Task StartAsync_WhenSecretPresent_DoesNotRegenerateIt()
    {
        var config = new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "keep-me" });

        await Build(Detector(available: true), config).StartAsync(CancellationToken.None);

        Assert.Equal("keep-me", config.Config.UnsubscribeSecret);
    }
}
