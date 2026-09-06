using System.Text.RegularExpressions;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Inject;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
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
    private static StartupService Build(
        Mock<IFileTransformationDetector> detector,
        FakeConfigAccessor config,
        FakeEasyNotifLog? easyNotifLog = null,
        FakeCampaignStore? campaigns = null)
        => new(
            NullLogger<StartupService>.Instance,
            detector.Object,
            config,
            easyNotifLog ?? new FakeEasyNotifLog(),
            campaigns ?? new FakeCampaignStore(),
            Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
            new FakeAddedItemsStore());

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

    [Fact]
    public async Task StartAsync_LogsAStartupEvent_ReflectingWhetherThePublicUrlIsSet()
    {
        var easyNotifLog = new FakeEasyNotifLog();
        var config = new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "set", PublicServerUrl = "https://media.example.org" });

        await Build(Detector(available: true), config, easyNotifLog).StartAsync(CancellationToken.None);

        var startup = Assert.Single(easyNotifLog.Entries, e => e.EventType == "plugin.startup");
        Assert.Equal("Info", startup.Level);
        Assert.Equal(true, startup.Fields!["publicServerUrlSet"]);
    }

    [Fact]
    public async Task StartAsync_WhenDetectorUnavailable_LogsAnErrorEvent()
    {
        var easyNotifLog = new FakeEasyNotifLog();
        var config = new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "set" });

        await Build(Detector(available: false), config, easyNotifLog).StartAsync(CancellationToken.None);

        var entry = Assert.Single(easyNotifLog.Entries, e => e.EventType == "filetransformation.missing");
        Assert.Equal("Error", entry.Level);
    }

    [Fact]
    public async Task StartAsync_WhenDetectorAvailable_LogsADetectedEvent()
    {
        var easyNotifLog = new FakeEasyNotifLog();
        var config = new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "set" });

        await Build(Detector(available: true), config, easyNotifLog).StartAsync(CancellationToken.None);

        Assert.Contains(easyNotifLog.Entries, e => e.EventType == "filetransformation.detected" && e.Level == "Info");
    }

    [Fact]
    public async Task StartAsync_SchedulesAnEnabledCampaignThatHasNoNextRun()
    {
        var campaigns = new FakeCampaignStore(new Campaign
        {
            Id = "newsletter",
            Schedule = RecurrenceSchedule.Daily(new TimeOnly(9, 0)),
            Enabled = true,
            NextRunUtc = null
        });
        var config = new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "set", SchedulerTimeZone = "Europe/Paris" });

        await Build(Detector(available: true), config, new FakeEasyNotifLog(), campaigns).StartAsync(CancellationToken.None);

        Assert.NotNull(campaigns.Campaigns[0].NextRunUtc);
        Assert.True(campaigns.Campaigns[0].NextRunUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task StartAsync_LeavesAPastNextRunUntouched()
    {
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var campaigns = new FakeCampaignStore(new Campaign
        {
            Id = "newsletter",
            Schedule = RecurrenceSchedule.Daily(new TimeOnly(9, 0)),
            Enabled = true,
            NextRunUtc = past
        });

        await Build(Detector(available: true), new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "set" }), new FakeEasyNotifLog(), campaigns)
            .StartAsync(CancellationToken.None);

        Assert.Equal(past, campaigns.Campaigns[0].NextRunUtc);
    }

    [Fact]
    public async Task StartAsync_DoesNotScheduleADisabledCampaign()
    {
        var campaigns = new FakeCampaignStore(new Campaign
        {
            Id = "newsletter",
            Schedule = RecurrenceSchedule.Daily(new TimeOnly(9, 0)),
            Enabled = false,
            NextRunUtc = null
        });

        await Build(Detector(available: true), new FakeConfigAccessor(new PluginConfiguration { UnsubscribeSecret = "set" }), new FakeEasyNotifLog(), campaigns)
            .StartAsync(CancellationToken.None);

        Assert.Null(campaigns.Campaigns[0].NextRunUtc);
    }

    [Fact]
    public async Task StopAsync_LogsAShutdownEvent()
    {
        var easyNotifLog = new FakeEasyNotifLog();
        var service = Build(Detector(available: true), new FakeConfigAccessor(), easyNotifLog);

        await service.StopAsync(CancellationToken.None);

        Assert.Contains(easyNotifLog.Entries, e => e.EventType == "plugin.shutdown" && e.Level == "Info");
    }
}
