using System.Reflection;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Controllers;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Inject;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Controllers;

/// <summary>
/// Covers <see cref="EasyNotifController"/>: the authorization attributes are present and correct
/// (reflection), the <c>me/*</c> endpoints take no user id (identity comes from
/// <see cref="IAuthorizationContext"/>), payloads are validated, the settings endpoints never leak
/// a secret and never overwrite one with a blank value, and <c>Wrap</c> maps storage/config
/// failures to 503.
///
/// Live policy enforcement is Jellyfin middleware, not this plugin's code; a non-admin getting 403
/// on the admin routes is verified by a manual test in Phase 3 (once the user panel exists).
/// </summary>
public sealed class EasyNotifControllerTests
{
    private static readonly Guid DefaultUserId = Guid.NewGuid();

    // -------------------------------------------------------------------------
    // Authorization attributes (reflection)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(EasyNotifController.GetAllPreferences))]
    [InlineData(nameof(EasyNotifController.PutUserPreferences))]
    [InlineData(nameof(EasyNotifController.GetSettings))]
    [InlineData(nameof(EasyNotifController.PutSettings))]
    [InlineData(nameof(EasyNotifController.GetStatus))]
    [InlineData(nameof(EasyNotifController.SendManualEmail))]
    [InlineData(nameof(EasyNotifController.StartManualBatch))]
    [InlineData(nameof(EasyNotifController.GetManualBatch))]
    [InlineData(nameof(EasyNotifController.GetLatestManualBatch))]
    [InlineData(nameof(EasyNotifController.RetryManualBatch))]
    [InlineData(nameof(EasyNotifController.GetLogs))]
    [InlineData(nameof(EasyNotifController.GetCampaigns))]
    [InlineData(nameof(EasyNotifController.PutCampaign))]
    [InlineData(nameof(EasyNotifController.RunCampaign))]
    [InlineData(nameof(EasyNotifController.PreviewCampaign))]
    [InlineData(nameof(EasyNotifController.GetTemplates))]
    [InlineData(nameof(EasyNotifController.GetTemplate))]
    [InlineData(nameof(EasyNotifController.CloneTemplate))]
    [InlineData(nameof(EasyNotifController.PutTemplate))]
    [InlineData(nameof(EasyNotifController.DeleteTemplate))]
    [InlineData(nameof(EasyNotifController.PreviewTemplate))]
    public void AdminEndpoints_RequireElevation(string methodName)
    {
        var authorize = Method(methodName).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(Policies.RequiresElevation, authorize!.Policy);
    }

    [Theory]
    [InlineData(nameof(EasyNotifController.GetMyPreferences))]
    [InlineData(nameof(EasyNotifController.PutMyPreferences))]
    [InlineData(nameof(EasyNotifController.GetMyContactEmail))]
    [InlineData(nameof(EasyNotifController.PutMyContactEmail))]
    [InlineData(nameof(EasyNotifController.SendMyTestEmail))]
    [InlineData(nameof(EasyNotifController.GetMySeries))]
    [InlineData(nameof(EasyNotifController.GetMySeriesState))]
    [InlineData(nameof(EasyNotifController.FollowSeries))]
    [InlineData(nameof(EasyNotifController.UnfollowSeries))]
    public void MeEndpoints_RequireAuthentication_WithoutElevation_NotAnonymous(string methodName)
    {
        var method = Method(methodName);
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.True(string.IsNullOrEmpty(authorize!.Policy), "me/* endpoints must not require elevation.");
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Theory]
    [InlineData(nameof(EasyNotifController.GetConfigScript))]
    [InlineData(nameof(EasyNotifController.GetStrings))]
    [InlineData(nameof(EasyNotifController.GetUserScript))]
    [InlineData(nameof(EasyNotifController.GetUserStylesheet))]
    public void AssetEndpoints_AreAnonymous(string methodName)
    {
        Assert.NotNull(Method(methodName).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Controller_DeclaresTheAuthMiddlewareResponsesForTheWholeApi()
    {
        var declared = typeof(EasyNotifController)
            .GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Select(a => a.StatusCode)
            .ToHashSet();

        Assert.Contains(StatusCodes.Status401Unauthorized, declared);
        Assert.Contains(StatusCodes.Status403Forbidden, declared);
    }

    [Fact]
    public void MeEndpoints_ExposeNoUserIdParameter()
    {
        var meActions = typeof(EasyNotifController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>()
                .Any(a => a.Template?.StartsWith("me/", StringComparison.Ordinal) == true))
            .ToList();

        Assert.Equal(9, meActions.Count);
        foreach (var action in meActions)
        {
            Assert.DoesNotContain(
                action.GetParameters(),
                p => p.Name is not null && p.Name.Contains("user", StringComparison.OrdinalIgnoreCase));
        }
    }

    // -------------------------------------------------------------------------
    // Caller identity
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetMyPreferences_PassesTheAuthenticatedUserIdToTheService()
    {
        var knownUserId = Guid.NewGuid();
        var service = new Mock<IPreferenceService>();
        service.Setup(s => s.GetOrCreate(It.IsAny<Guid>())).Returns(new UserPreference());
        var controller = BuildController(service, auth: AuthReturning(knownUserId));

        await controller.GetMyPreferences();

        service.Verify(s => s.GetOrCreate(knownUserId), Times.Once);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GetMyPreferences_ReportsAnnouncementsAsTheInverseOfTheOptOut(bool optOut, bool expected)
    {
        var service = new Mock<IPreferenceService>();
        service.Setup(s => s.GetOrCreate(It.IsAny<Guid>())).Returns(new UserPreference { AnnouncementsOptOut = optOut });
        var controller = BuildController(service, auth: AuthReturning(Guid.NewGuid()));

        var ok = Assert.IsType<OkObjectResult>(await controller.GetMyPreferences());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal(expected, json.RootElement.GetProperty("announcements").GetBoolean());
    }

    // -------------------------------------------------------------------------
    // Followed series
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SeriesEndpoints_ActOnTheAuthenticatedUser()
    {
        var caller = Guid.NewGuid();
        var series = Guid.NewGuid();
        var followed = new Mock<IFollowedSeriesService>();
        followed.Setup(f => f.List(It.IsAny<Guid>())).Returns([]);
        var controller = BuildController(auth: AuthReturning(caller), followedSeries: followed);

        await controller.GetMySeries();
        await controller.GetMySeriesState(series);
        await controller.FollowSeries(series, null);
        await controller.UnfollowSeries(series);

        followed.Verify(f => f.List(caller), Times.Once);
        followed.Verify(f => f.IsFollowing(caller, series), Times.Once);
        followed.Verify(f => f.Follow(caller, series, "bell"), Times.Once);
        followed.Verify(f => f.Unfollow(caller, series), Times.Once);
    }

    [Fact]
    public async Task SeriesEndpoints_AnswerABareNotFound_WhenTheServiceRefuses()
    {
        var followed = new Mock<IFollowedSeriesService>();
        followed.Setup(f => f.IsFollowing(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns((bool?)null);
        var controller = BuildController(followedSeries: followed);
        var series = Guid.NewGuid();

        Assert.IsType<NotFoundResult>(await controller.GetMySeriesState(series));
        Assert.IsType<NotFoundResult>(await controller.FollowSeries(series, "panel"));
        Assert.IsType<NotFoundResult>(await controller.UnfollowSeries(series));
    }

    [Fact]
    public async Task SeriesEndpoints_ReportTheStateAndNoContentOnSuccess()
    {
        var followed = new Mock<IFollowedSeriesService>();
        followed.Setup(f => f.IsFollowing(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns(true);
        followed.Setup(f => f.Follow(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>())).Returns(true);
        followed.Setup(f => f.Unfollow(It.IsAny<Guid>(), It.IsAny<Guid>())).Returns(true);
        var controller = BuildController(followedSeries: followed);
        var series = Guid.NewGuid();

        var ok = Assert.IsType<OkObjectResult>(await controller.GetMySeriesState(series));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.True(json.RootElement.GetProperty("following").GetBoolean());
        Assert.IsType<NoContentResult>(await controller.FollowSeries(series, null));
        Assert.IsType<NoContentResult>(await controller.UnfollowSeries(series));
    }

    [Theory]
    [InlineData(null, "bell")]
    [InlineData("panel", "panel")]
    [InlineData("email", "bell")]
    [InlineData("<script>", "bell")]
    public async Task FollowSeries_KeepsOnlyAKnownSource(string? source, string expected)
    {
        var followed = new Mock<IFollowedSeriesService>();
        var controller = BuildController(followedSeries: followed);

        await controller.FollowSeries(Guid.NewGuid(), source);

        followed.Verify(f => f.Follow(It.IsAny<Guid>(), It.IsAny<Guid>(), expected), Times.Once);
    }

    [Fact]
    public async Task GetMySeries_Returns503_WhenStorageIsUnavailable()
    {
        var followed = new Mock<IFollowedSeriesService>();
        followed.Setup(f => f.List(It.IsAny<Guid>())).Throws(new IOException("disk"));
        var controller = BuildController(followedSeries: followed);

        var result = Assert.IsAssignableFrom<IStatusCodeActionResult>(await controller.GetMySeries());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Fact]
    public async Task PutMyPreferences_Announcements_WritesTheOptOut()
    {
        var userId = Guid.NewGuid();
        var service = new Mock<IPreferenceService>();
        var controller = BuildController(service, auth: AuthReturning(userId));

        await controller.PutMyPreferences(new CategoryUpdate { Announcements = false });

        service.Verify(s => s.SetCategories(userId, It.Is<IReadOnlyDictionary<EmailCategory, bool>>(m => m.Count == 0), true, null), Times.Once);
    }

    [Fact]
    public async Task PutMyPreferences_Episodes_WritesTheOptOut()
    {
        var userId = Guid.NewGuid();
        var service = new Mock<IPreferenceService>();
        var controller = BuildController(service, auth: AuthReturning(userId));

        await controller.PutMyPreferences(new CategoryUpdate { Episodes = false });

        service.Verify(s => s.SetCategories(userId, It.Is<IReadOnlyDictionary<EmailCategory, bool>>(m => m.Count == 0), null, true), Times.Once);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GetMyPreferences_ReportsEpisodesAsTheInverseOfTheOptOut(bool optOut, bool expected)
    {
        var service = new Mock<IPreferenceService>();
        service.Setup(s => s.GetOrCreate(It.IsAny<Guid>())).Returns(new UserPreference { EpisodesOptOut = optOut });
        var controller = BuildController(service, auth: AuthReturning(Guid.NewGuid()));

        var ok = Assert.IsType<OkObjectResult>(await controller.GetMyPreferences());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal(expected, json.RootElement.GetProperty("episodes").GetBoolean());
    }

    [Fact]
    public void PutUserPreferences_IgnoresAnnouncementsAndEpisodes_AnAdminCannotOverrideAnOptOut()
    {
        var service = new Mock<IPreferenceService>();
        var controller = BuildController(service);

        controller.PutUserPreferences(Guid.NewGuid(), new AdminPreferenceUpdate { Announcements = true, Episodes = true });

        service.Verify(s => s.SetCategories(It.IsAny<Guid>(), It.IsAny<IReadOnlyDictionary<EmailCategory, bool>>(), It.IsAny<bool?>(), It.IsAny<bool?>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Payload validation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PutMyContactEmail_WhenTheServiceRejectsTheAddress_Returns400()
    {
        var service = new Mock<IPreferenceService>();
        service.Setup(s => s.SetContactEmail(It.IsAny<Guid>(), "nope"))
            .Throws(new ArgumentException("bad"));
        var controller = BuildController(service);

        var result = await controller.PutMyContactEmail(new ContactEmailUpdate { Email = "nope" });

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task PutMyContactEmail_WithNull_Returns204()
    {
        var controller = BuildController();

        var result = await controller.PutMyContactEmail(new ContactEmailUpdate { Email = null });

        Assert.IsType<NoContentResult>(result);
    }

    [Theory]
    [InlineData("Not/A/Zone")]
    [InlineData("Mars/Standard_Time")]
    public void PutSettings_WithAnUnknownTimeZone_Returns400(string tz)
    {
        var result = BuildController().PutSettings(new SettingsUpdate { SchedulerTimeZone = tz });

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public void PutSettings_WithAMalformedFromEmail_Returns400()
    {
        var result = BuildController().PutSettings(new SettingsUpdate { FromEmail = "not-an-email" });

        Assert.IsType<BadRequestResult>(result);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://evil.example.org")]
    [InlineData("//evil.example.org")]
    [InlineData("https://ok.example.org\r\nX-Injected: 1")]
    public void PutSettings_WithAMalformedPublicServerUrl_Returns400(string url)
    {
        var result = BuildController().PutSettings(new SettingsUpdate { PublicServerUrl = url });

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public void PutSettings_WithABlankPublicServerUrl_Returns204()
    {
        var result = BuildController().PutSettings(new SettingsUpdate { PublicServerUrl = "  " });

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public void Settings_RoundTripTheInjectionSwitches_AndKeepThemWhenOmitted()
    {
        var config = new FakeConfig();
        var controller = BuildController(config: config);

        Assert.IsType<NoContentResult>(controller.PutSettings(new SettingsUpdate { InjectWebUi = false, InjectSeriesBell = false }));
        Assert.False(config.Config.InjectWebUi);
        Assert.False(config.Config.InjectSeriesBell);

        controller.PutSettings(new SettingsUpdate());
        Assert.False(config.Config.InjectWebUi);

        var ok = Assert.IsType<OkObjectResult>(controller.GetSettings());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.False(json.RootElement.GetProperty("injectWebUi").GetBoolean());
        Assert.False(json.RootElement.GetProperty("injectSeriesBell").GetBoolean());
    }

    [Fact]
    public void InjectionSwitches_DefaultToOn()
    {
        var cfg = new PluginConfiguration();

        Assert.True(cfg.InjectWebUi);
        Assert.True(cfg.InjectSeriesBell);
    }

    // -------------------------------------------------------------------------
    // Settings: secrets never leak, blank never overwrites
    // -------------------------------------------------------------------------

    [Fact]
    public void GetSettings_NeverReturnsASecretValue()
    {
        var config = new FakeConfig(new PluginConfiguration
        {
            ResendApiKey = "re_supersecret",
            WebhookSigningSecret = "whsec_supersecret",
            FromEmail = "from@example.org"
        });
        var controller = BuildController(config: config);

        var ok = Assert.IsType<OkObjectResult>(controller.GetSettings());
        var json = JsonSerializer.Serialize(ok.Value);

        Assert.DoesNotContain("supersecret", json, StringComparison.Ordinal);
        Assert.Contains("\"resendApiKeySet\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"webhookSigningSecretSet\":true", json, StringComparison.Ordinal);
        Assert.Contains("from@example.org", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PutSettings_WithABlankSecret_KeepsTheStoredOne(string? blank)
    {
        var config = new FakeConfig(new PluginConfiguration { ResendApiKey = "re_existing" });
        var controller = BuildController(config: config);

        var result = controller.PutSettings(new SettingsUpdate { ResendApiKey = blank, FromName = "X" });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("re_existing", config.Config.ResendApiKey);
        Assert.Equal(1, config.SaveCount);
    }

    [Fact]
    public void PutSettings_WithANewSecret_OverwritesIt()
    {
        var secrets = new FakeSecretStore(resendApiKey: "re_old");
        var controller = BuildController(secrets: secrets);

        controller.PutSettings(new SettingsUpdate { ResendApiKey = " re_new " });

        Assert.Equal("re_new", secrets.ResendApiKey);
    }

    [Fact]
    public void PutSettings_AppliesTheNonSecretFields()
    {
        var config = new FakeConfig();
        var controller = BuildController(config: config);

        controller.PutSettings(new SettingsUpdate
        {
            FromEmail = "from@example.org",
            FromName = "Sender",
            ReplyTo = "reply@example.org",
            PublicServerUrl = "https://media.example.org",
            SchedulerTimeZone = "Europe/Paris"
        });

        Assert.Equal("from@example.org", config.Config.FromEmail);
        Assert.Equal("Sender", config.Config.FromName);
        Assert.Equal("reply@example.org", config.Config.ReplyTo);
        Assert.Equal("https://media.example.org", config.Config.PublicServerUrl);
        Assert.Equal("Europe/Paris", config.Config.SchedulerTimeZone);
    }

    [Fact]
    public void PutSettings_StoresTheNewsletterCaps_AndANullCapIsLeftUnchanged()
    {
        var config = new FakeConfig();
        var controller = BuildController(config: config);

        Assert.IsType<NoContentResult>(controller.PutSettings(new SettingsUpdate { NewsletterMaxMovies = 10, NewsletterMaxSeries = 200 }));
        Assert.IsType<NoContentResult>(controller.PutSettings(new SettingsUpdate { NewsletterMaxMovies = 5 }));

        Assert.Equal(5, config.Config.NewsletterMaxMovies);
        Assert.Equal(200, config.Config.NewsletterMaxSeries);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 201)]
    [InlineData(-3, 30)]
    public void PutSettings_WithANewsletterCapOutOfRange_Returns400(int? maxMovies, int? maxSeries)
    {
        var config = new FakeConfig();

        var result = BuildController(config: config).PutSettings(new SettingsUpdate { NewsletterMaxMovies = maxMovies, NewsletterMaxSeries = maxSeries });

        Assert.IsType<BadRequestResult>(result);
        Assert.Equal(PluginConfiguration.DefaultNewsletterCap, config.Config.NewsletterMaxMovies);
        Assert.Equal(PluginConfiguration.DefaultNewsletterCap, config.Config.NewsletterMaxSeries);
    }

    [Fact]
    public void PutSettings_StoresTheQuotaLimits_ZeroDailyAllowed_AndGetSettingsReturnsThem()
    {
        var config = new FakeConfig();
        var controller = BuildController(config: config);

        Assert.IsType<NoContentResult>(controller.PutSettings(new SettingsUpdate { QuotaDailyLimit = 0, QuotaMonthlyLimit = 50000 }));
        Assert.IsType<NoContentResult>(controller.PutSettings(new SettingsUpdate { QuotaMonthlyLimit = 40000 }));

        Assert.Equal(0, config.Config.QuotaDailyLimit);
        Assert.Equal(40000, config.Config.QuotaMonthlyLimit);
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(controller.GetSettings()).Value);
        Assert.Contains("\"quotaDailyLimit\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"quotaMonthlyLimit\":40000", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(100001, null)]
    [InlineData(null, 0)]
    [InlineData(null, 1000001)]
    public void PutSettings_WithAQuotaLimitOutOfRange_Returns400(int? daily, int? monthly)
    {
        var config = new FakeConfig();

        var result = BuildController(config: config).PutSettings(new SettingsUpdate { QuotaDailyLimit = daily, QuotaMonthlyLimit = monthly });

        Assert.IsType<BadRequestResult>(result);
        Assert.Equal(PluginConfiguration.DefaultQuotaDailyLimit, config.Config.QuotaDailyLimit);
        Assert.Equal(PluginConfiguration.DefaultQuotaMonthlyLimit, config.Config.QuotaMonthlyLimit);
    }

    [Fact]
    public void GetStatus_ReportsAProviderSuspension()
    {
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(40, 12, 3000, 100, false, true)
        {
            SuspendedUntilUtc = new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc)
        });
        var sendLog = new Mock<ISendLog>();
        sendLog.Setup(l => l.LastWebhook()).Returns((default(DateTime?), default(string)));
        sendLog.Setup(l => l.Recent(It.IsAny<int>())).Returns([]);

        var ok = Assert.IsType<OkObjectResult>(BuildController(quota: quota, sendLog: sendLog).GetStatus());

        Assert.Contains("\"suspendedUntilUtc\":\"2026-06-16T00:00:00Z\"", JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_ReportsTheReserve_OfACampaignDueToday()
    {
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
        var sendLog = new Mock<ISendLog>();
        sendLog.Setup(l => l.LastWebhook()).Returns((default(DateTime?), default(string)));
        sendLog.Setup(l => l.Recent(It.IsAny<int>())).Returns([]);

        // Overdue, hence before the next 00:00 UTC whatever the time the test runs.
        var campaigns = new FakeCampaignStore(new Campaign { Id = "newsletter", Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-5) });
        var ok = Assert.IsType<OkObjectResult>(BuildController(quota: quota, sendLog: sendLog, campaigns: campaigns).GetStatus());

        Assert.Contains("\"reserve\":40", JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void PutSettings_OnSuccess_LogsAnUpdateEvent_WithoutTheKey()
    {
        var easyNotifLog = new FakeEasyNotifLog();
        var controller = BuildController(easyNotifLog: easyNotifLog);

        controller.PutSettings(new SettingsUpdate { FromEmail = "from@example.org", ResendApiKey = "re_supersecret" });

        var entry = Assert.Single(easyNotifLog.Entries);
        Assert.Equal("Info", entry.Level);
        Assert.Equal("settings.updated", entry.EventType);
        Assert.Equal(true, entry.Fields!["resendApiKeySet"]);
        Assert.DoesNotContain(entry.Fields.Values, v => Equals(v, "re_supersecret"));
    }

    [Fact]
    public void GetLogs_WhenNoLogFileExists_ReturnsEmptyArray()
    {
        var ok = Assert.IsType<OkObjectResult>(BuildController().GetLogs());

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(ok.Value));
    }

    [Fact]
    public void GetStrings_ForAnUnknownLanguage_Returns404()
    {
        Assert.IsType<NotFoundResult>(BuildController().GetStrings("de"));
    }

    [Fact]
    public void GetStatus_NeverReturnsASecretValue()
    {
        var config = new FakeConfig(new PluginConfiguration
        {
            ResendApiKey = "re_supersecret",
            WebhookSigningSecret = "whsec_supersecret",
            FromEmail = "from@example.org"
        });
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(3, 1, 3000, 100, false, false));
        var sendLog = new Mock<ISendLog>();
        sendLog.Setup(l => l.LastWebhook()).Returns((default(DateTime?), default(string)));
        sendLog.Setup(l => l.Recent(It.IsAny<int>())).Returns([]);
        var controller = BuildController(config: config, quota: quota, sendLog: sendLog);

        var ok = Assert.IsType<OkObjectResult>(controller.GetStatus());
        var json = JsonSerializer.Serialize(ok.Value);

        Assert.DoesNotContain("supersecret", json, StringComparison.Ordinal);
        Assert.Contains("\"configured\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_ReportsNotConfigured_WhenTheApiKeyIsMissing()
    {
        var config = new FakeConfig(new PluginConfiguration { FromEmail = "from@example.org" });
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
        var sendLog = new Mock<ISendLog>();
        sendLog.Setup(l => l.LastWebhook()).Returns((default(DateTime?), default(string)));
        sendLog.Setup(l => l.Recent(It.IsAny<int>())).Returns([]);
        var controller = BuildController(config: config, quota: quota, sendLog: sendLog);

        var ok = Assert.IsType<OkObjectResult>(controller.GetStatus());

        Assert.Contains("\"configured\":false", JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_IncludesCampaigns_FileTransformationState_AndPublicUrlFlag()
    {
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
        var sendLog = new Mock<ISendLog>();
        sendLog.Setup(l => l.LastWebhook()).Returns((default(DateTime?), default(string)));
        sendLog.Setup(l => l.Recent(It.IsAny<int>())).Returns([]);
        var ft = new Mock<IFileTransformationDetector>();
        ft.Setup(d => d.IsAvailable()).Returns(true);

        var controller = BuildController(
            config: new FakeConfig(new PluginConfiguration { PublicServerUrl = "https://media.example.org" }),
            quota: quota,
            sendLog: sendLog,
            campaigns: SeededCampaigns(),
            fileTransformation: ft);

        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(controller.GetStatus()).Value);

        Assert.Contains("\"fileTransformation\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"publicUrlSet\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"newsletter\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"weekly-recap\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetStatus_ReportsALostDrainBatch_AndTheCampaignError()
    {
        var added = new FakeAddedItemsStore();
        added.Health.Lost(4, new IOException("disk full"));
        var episodes = new Mock<ISeriesEpisodeQueue>();
        episodes.Setup(q => q.Health).Returns(new DrainHealth("episodes", new FakeEasyNotifLog(), () => 7));
        var drains = new DrainStatus(added, new FakePlaybackHistoryStore(), episodes.Object);
        var campaigns = SeededCampaigns();
        campaigns.Campaigns[0].LastError = "InvalidOperationException: template missing";
        campaigns.Campaigns[0].LastErrorUtc = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);
        var sendLog = new Mock<ISendLog>();
        sendLog.Setup(l => l.Recent(It.IsAny<int>())).Returns([]);

        var json = JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(BuildController(sendLog: sendLog, campaigns: campaigns, drains: drains).GetStatus()).Value);

        Assert.Contains("\"newItems\":{\"lastFlushUtc\":null,\"lostBatches\":1,\"lostItems\":4,\"dropped\":0,\"depth\":0}", json, StringComparison.Ordinal);
        Assert.Contains("\"episodes\":{\"lastFlushUtc\":null,\"lostBatches\":0,\"lostItems\":0,\"dropped\":0,\"depth\":7}", json, StringComparison.Ordinal);
        Assert.Contains("\"playback\":{", json, StringComparison.Ordinal);
        Assert.Contains("\"lastError\":\"InvalidOperationException: template missing\",\"lastErrorUtc\":\"2026-10-09T08:00:00Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSettings_IncludesTheStartupWarning()
    {
        var config = new FakeConfig(new PluginConfiguration { StartupWarning = "FileTransformation missing" });
        var controller = BuildController(config: config);

        var ok = Assert.IsType<OkObjectResult>(controller.GetSettings());
        var json = JsonSerializer.Serialize(ok.Value);

        Assert.Contains("FileTransformation missing", json, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // me/test: real send, quota and log
    // -------------------------------------------------------------------------

    private static Mock<IPreferenceService> ServiceWithContactEmail(string? email)
    {
        var service = new Mock<IPreferenceService>();
        service.Setup(s => s.GetOrCreate(It.IsAny<Guid>()))
            .Returns(new UserPreference { ContactEmail = email });
        return service;
    }

    [Fact]
    public async Task SendMyTestEmail_WithoutAContactAddress_Returns400_AndDoesNotSend()
    {
        var email = new Mock<IEmailSender>();
        var controller = BuildController(ServiceWithContactEmail(null), emailSender: email);

        var result = await controller.SendMyTestEmail(new TestEmailRequest());

        Assert.IsType<BadRequestObjectResult>(result);
        email.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMyTestEmail_OnSuccess_Sends_RecordsQuota_AndLogsMasked()
    {
        var email = new Mock<IEmailSender>();
        email.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult(true, "re_1", 200, null));
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
        var sendLog = new Mock<ISendLog>();
        SendLogEntry? logged = null;
        sendLog.Setup(l => l.Append(It.IsAny<SendLogEntry>())).Callback<SendLogEntry>(e => logged = e);

        var controller = BuildController(
            ServiceWithContactEmail("someone@example.org"),
            emailSender: email,
            quota: quota,
            sendLog: sendLog);

        var result = await controller.SendMyTestEmail(new TestEmailRequest { Lang = "en" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("ok = True", ok.Value!.ToString(), StringComparison.Ordinal);
        email.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        quota.Verify(q => q.RecordSend(), Times.Once);
        Assert.NotNull(logged);
        Assert.Equal("sent", logged!.Status);
        Assert.Equal("test", logged.Context);
        Assert.DoesNotContain("someone@example.org", logged.ToMasked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMyTestEmail_WhenQuotaIsOver_Returns503_AndSendsNothing()
    {
        var email = new Mock<IEmailSender>();
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(3000, 100, 3000, 100, true, true));

        var result = await BuildController(ServiceWithContactEmail("a@b.co"), emailSender: email, quota: quota)
            .SendMyTestEmail(new TestEmailRequest());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
        email.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMyTestEmail_WithinTheCooldown_Returns429_AndSendsOnce()
    {
        var email = new Mock<IEmailSender>();
        email.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult(true, "re_1", 200, null));
        var controller = BuildController(ServiceWithContactEmail("a@b.co"), emailSender: email);

        var first = await controller.SendMyTestEmail(new TestEmailRequest());
        var second = await controller.SendMyTestEmail(new TestEmailRequest());

        Assert.IsType<OkObjectResult>(first);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(second).StatusCode);
        email.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendManualEmail_WhenQuotaIsOver_Returns503_AndDoesNotCallTheService()
    {
        var manual = new Mock<IManualEmailService>();
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(3000, 100, 3000, 100, true, true));

        var result = await BuildController(quota: quota, manualEmail: manual)
            .SendManualEmail(new ManualSendRequest { Subject = "hi", Text = "x" });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
        manual.Verify(m => m.SendAsync(It.IsAny<ManualEmailRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMyTestEmail_OnFailure_DoesNotRecordQuota_AndLogsFailed()
    {
        var email = new Mock<IEmailSender>();
        email.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult(false, null, 422, "bad"));
        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
        var sendLog = new Mock<ISendLog>();
        SendLogEntry? logged = null;
        sendLog.Setup(l => l.Append(It.IsAny<SendLogEntry>())).Callback<SendLogEntry>(e => logged = e);

        var controller = BuildController(
            ServiceWithContactEmail("someone@example.org"),
            emailSender: email,
            quota: quota,
            sendLog: sendLog);

        var result = await controller.SendMyTestEmail(new TestEmailRequest());

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("ok = False", ok.Value!.ToString(), StringComparison.Ordinal);
        quota.Verify(q => q.RecordSend(), Times.Never);
        Assert.Equal("failed", logged!.Status);
    }

    [Fact]
    public async Task SendMyTestEmail_UsesTheRequestedLanguageForTheSubject()
    {
        var captured = new List<EmailMessage>();
        var email = new Mock<IEmailSender>();
        email.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendResult(true, "re_1", 200, null))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured.Add(m));

        var fr = BuildController(ServiceWithContactEmail("a@b.co"), emailSender: email);
        await fr.SendMyTestEmail(new TestEmailRequest { Lang = "fr" });
        var en = BuildController(ServiceWithContactEmail("a@b.co"), emailSender: email);
        await en.SendMyTestEmail(new TestEmailRequest { Lang = "en" });

        Assert.NotEqual(captured[0].Subject, captured[1].Subject);
    }

    // -------------------------------------------------------------------------
    // admin/send: manual email
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendManualEmail_WithNullBody_Returns400()
    {
        var result = await BuildController().SendManualEmail(null);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SendManualEmail_WithBlankSubject_Returns400_AndDoesNotCallTheService()
    {
        var manual = new Mock<IManualEmailService>();
        var controller = BuildController(manualEmail: manual);

        var result = await controller.SendManualEmail(new ManualSendRequest { Subject = "  ", Text = "x" });

        Assert.IsType<BadRequestObjectResult>(result);
        manual.Verify(m => m.SendAsync(It.IsAny<ManualEmailRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendManualEmail_WithUndecodableAttachment_Returns400()
    {
        var result = await BuildController().SendManualEmail(new ManualSendRequest
        {
            Subject = "Hi",
            Text = "x",
            Attachments = [new AttachmentDto { FileName = "a.bin", ContentBase64 = "not base64!!!" }]
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SendManualEmail_OnSuccess_ReturnsTheSummary()
    {
        var manual = new Mock<IManualEmailService>();
        manual.Setup(m => m.SendAsync(It.IsAny<ManualEmailRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManualEmailResult(2, 0, 1, [new ManualSendDetail("a***e@example.org", "sent")]));
        var controller = BuildController(manualEmail: manual);

        var ok = Assert.IsType<OkObjectResult>(await controller.SendManualEmail(new ManualSendRequest
        {
            Subject = "Hi",
            Text = "Body",
            RecipientMode = "all"
        }));
        var json = JsonSerializer.Serialize(ok.Value);

        Assert.Contains("\"sent\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"skippedNoEmail\":1", json, StringComparison.Ordinal);
        Assert.Contains("a***e@example.org", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendManualEmail_WhenTheServiceRejectsTheRequest_Returns400()
    {
        var manual = new Mock<IManualEmailService>();
        manual.Setup(m => m.SendAsync(It.IsAny<ManualEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Select at least one user."));
        var controller = BuildController(manualEmail: manual);

        var result = await controller.SendManualEmail(new ManualSendRequest { Subject = "Hi", Text = "x", RecipientMode = "selected" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SendManualEmail_WhenStorageFails_Returns503()
    {
        var manual = new Mock<IManualEmailService>();
        manual.Setup(m => m.SendAsync(It.IsAny<ManualEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk gone"));
        var controller = BuildController(manualEmail: manual);

        var result = await controller.SendManualEmail(new ManualSendRequest { Subject = "Hi", Text = "x" });

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    // -------------------------------------------------------------------------
    // admin/send/batches: background manual lots
    // -------------------------------------------------------------------------

    private static ManualBatchSnapshot Batch(Guid id, string status = "running")
        => new(id, status, 3, 1, 0, 0, 2, [new ManualSendDetail("a***e@example.org", "sent")], DateTime.UtcNow, null);

    [Fact]
    public void StartManualBatch_Returns202_WithTheLot()
    {
        var id = Guid.NewGuid();
        var queue = new Mock<IManualSendQueue>();
        queue.Setup(q => q.Start(It.IsAny<ManualEmailRequest>())).Returns(Batch(id));
        var controller = BuildController(manualQueue: queue);

        var accepted = Assert.IsType<AcceptedResult>(controller.StartManualBatch(new ManualSendRequest { Subject = "Hi", Text = "x" }));
        var json = JsonSerializer.Serialize(accepted.Value);

        Assert.Equal($"/EasyNotif/admin/send/batches/{id}", accepted.Location);
        Assert.Contains("\"status\":\"running\"", json, StringComparison.Ordinal);
        Assert.Contains("\"total\":3", json, StringComparison.Ordinal);
        Assert.Contains("\"processed\":1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartManualBatch_InvalidRequest_Returns400_QuotaOver_Returns503()
    {
        var queue = new Mock<IManualSendQueue>();
        queue.Setup(q => q.Start(It.IsAny<ManualEmailRequest>())).Throws(new ArgumentException("Select at least one user."));
        Assert.IsType<BadRequestObjectResult>(BuildController(manualQueue: queue).StartManualBatch(new ManualSendRequest { Subject = "Hi", Text = "x" }));
        Assert.IsType<BadRequestObjectResult>(BuildController(manualQueue: queue).StartManualBatch(new ManualSendRequest { Text = "x" }));

        var quota = new Mock<IQuotaGuard>();
        quota.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(3000, 100, 3000, 100, true, true));
        var over = BuildController(quota: quota, manualQueue: queue).StartManualBatch(new ManualSendRequest { Subject = "Hi", Text = "x" });
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(over).StatusCode);
    }

    [Fact]
    public void GetManualBatch_UnknownId_Returns404_LatestWithNone_Returns204()
    {
        var controller = BuildController();

        Assert.IsType<NotFoundResult>(controller.GetManualBatch(Guid.NewGuid()));
        Assert.IsType<NoContentResult>(controller.GetLatestManualBatch());
    }

    [Theory]
    [InlineData(ManualRetryOutcome.Started, StatusCodes.Status202Accepted)]
    [InlineData(ManualRetryOutcome.Running, StatusCodes.Status409Conflict)]
    [InlineData(ManualRetryOutcome.NothingToRetry, StatusCodes.Status409Conflict)]
    [InlineData(ManualRetryOutcome.NotFound, StatusCodes.Status404NotFound)]
    public void RetryManualBatch_MapsTheOutcome(ManualRetryOutcome outcome, int expected)
    {
        var id = Guid.NewGuid();
        var queue = new Mock<IManualSendQueue>();
        queue.Setup(q => q.Retry(id)).Returns((outcome, outcome == ManualRetryOutcome.NotFound ? null : Batch(id)));

        var result = BuildController(manualQueue: queue).RetryManualBatch(id);

        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode);
    }

    // -------------------------------------------------------------------------
    // Wrap: storage/config failures map to 503
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(WrapFailures))]
    public void Wrap_WhenActionThrowsAStorageFailure_Returns503(Exception thrown)
    {
        var result = InvokeWrap(BuildController(), () => throw thrown);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    public static IEnumerable<object[]> WrapFailures =>
    [
        [new IOException("disk gone")],
        [new UnauthorizedAccessException()],
        [new InvalidOperationException("plugin instance unavailable")]
    ];

    [Fact]
    public void Wrap_WhenActionSucceeds_ReturnsItsResult()
    {
        var expected = new OkResult();

        var result = InvokeWrap(BuildController(), () => expected);

        Assert.Same(expected, result);
    }

    // -------------------------------------------------------------------------
    // Admin - campaigns
    // -------------------------------------------------------------------------

    private static FakeCampaignStore SeededCampaigns() => new(
        new Campaign { Id = "newsletter", Type = CampaignType.Newsletter, Category = EmailCategory.News, Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Friday, new TimeOnly(9, 0)) },
        new Campaign { Id = "weekly-recap", Type = CampaignType.WeeklyRecap, Category = EmailCategory.Recap, Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(8, 0)) });

    [Fact]
    public void GetCampaigns_ReturnsBothRows_WithNestedSchedule_NoSecret()
    {
        var controller = BuildController(campaigns: SeededCampaigns());

        var ok = Assert.IsType<OkObjectResult>(controller.GetCampaigns());
        var json = JsonSerializer.Serialize(ok.Value);

        Assert.Contains("newsletter", json, StringComparison.Ordinal);
        Assert.Contains("weekly-recap", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"Weekly\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("re_", json, StringComparison.Ordinal);
        Assert.DoesNotContain("whsec_", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetCampaigns_ConvertsRunTimesToTheConfiguredTimeZone()
    {
        var campaigns = SeededCampaigns();
        campaigns.Campaigns[0].NextRunUtc = new DateTime(2026, 6, 15, 13, 45, 0, DateTimeKind.Utc);
        var controller = BuildController(
            config: new FakeConfig(new PluginConfiguration { SchedulerTimeZone = "Europe/Paris" }),
            campaigns: campaigns);

        var ok = Assert.IsType<OkObjectResult>(controller.GetCampaigns());
        var json = JsonSerializer.Serialize(ok.Value);

        // 13:45 UTC in June is 15:45 in Paris (CEST).
        Assert.Contains("\"nextRunLocal\":\"2026-06-15 15:45\"", json, StringComparison.Ordinal);
        Assert.Contains("\"timeZone\":\"Europe/Paris\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PutCampaign_UnknownId_Returns404()
    {
        var controller = BuildController(campaigns: SeededCampaigns());

        var result = controller.PutCampaign("nope", new CampaignUpdate { Enabled = true });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void PutCampaign_InvalidLanguage_Returns400()
    {
        var result = BuildController(campaigns: SeededCampaigns()).PutCampaign("newsletter", new CampaignUpdate { MailLanguage = "de" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("Weekly", null, "09:00")]
    [InlineData("Weekly", "Friday", "99:99")]
    [InlineData("Monthly", null, "09:00")]
    public void PutCampaign_InvalidSchedule_Returns400(string kind, string? dayOfWeek, string time)
    {
        var body = new CampaignUpdate
        {
            Schedule = new ScheduleUpdate { Kind = kind, Time = time, DayOfWeek = dayOfWeek }
        };

        var result = BuildController(campaigns: SeededCampaigns()).PutCampaign("newsletter", body);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void PutCampaign_IntervalDaysOutOfRange_Returns400(int intervalDays)
    {
        var body = new CampaignUpdate
        {
            Schedule = new ScheduleUpdate { Kind = "EveryNDays", Time = "09:00", IntervalDays = intervalDays }
        };

        var result = BuildController(campaigns: SeededCampaigns()).PutCampaign("newsletter", body);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutCampaign_ValidScheduleChange_Returns204_RecomputesNextRun()
    {
        var campaigns = SeededCampaigns();
        campaigns.Campaigns[0].Enabled = true;
        var controller = BuildController(campaigns: campaigns);

        var result = controller.PutCampaign("newsletter", new CampaignUpdate
        {
            Schedule = new ScheduleUpdate { Kind = "daily", Time = "07:15" }
        });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(RecurrenceKind.Daily, campaigns.Campaigns[0].Schedule.Kind);
        Assert.NotNull(campaigns.Campaigns[0].NextRunUtc);
    }

    [Fact]
    public void PutCampaign_Disabling_ClearsNextRun()
    {
        var campaigns = SeededCampaigns();
        campaigns.Campaigns[0].Enabled = true;
        campaigns.Campaigns[0].NextRunUtc = DateTime.UtcNow.AddDays(1);

        BuildController(campaigns: campaigns).PutCampaign("newsletter", new CampaignUpdate { Enabled = false });

        Assert.Null(campaigns.Campaigns[0].NextRunUtc);
    }

    [Fact]
    public async Task RunCampaign_DelegatesToDispatch_MapsFoundToResult()
    {
        var dispatch = new Mock<IDispatchService>();
        dispatch.Setup(d => d.RunCampaignNowAsync("newsletter", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignRunResult("newsletter", 2, 0, 1, DateTime.UtcNow) { Found = true });

        var ok = Assert.IsType<OkObjectResult>(await BuildController(dispatch: dispatch).RunCampaign("newsletter"));
        Assert.Contains("\"sent\":2", JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCampaign_UnknownId_Returns404()
    {
        var dispatch = new Mock<IDispatchService>();
        dispatch.Setup(d => d.RunCampaignNowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignRunResult("x", 0, 0, 0, null) { Found = false });

        Assert.IsType<NotFoundResult>(await BuildController(dispatch: dispatch).RunCampaign("x"));
    }

    [Fact]
    public async Task PreviewCampaign_WithoutAContactAddress_Returns400()
    {
        var controller = BuildController(ServiceWithContactEmail(null));

        var result = await controller.PreviewCampaign("newsletter");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("no-contact-email", JsonSerializer.Serialize(bad.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewCampaign_UnknownId_Returns404()
    {
        var dispatch = new Mock<IDispatchService>();
        dispatch.Setup(d => d.PreviewAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignPreviewResult(false, false, 0, 0));

        var result = await BuildController(ServiceWithContactEmail("admin@example.org"), dispatch: dispatch).PreviewCampaign("x");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PreviewCampaign_Success_ReturnsDigestCounts()
    {
        var dispatch = new Mock<IDispatchService>();
        dispatch.Setup(d => d.PreviewAsync("newsletter", "admin@example.org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CampaignPreviewResult(true, true, 3, 1));

        var ok = Assert.IsType<OkObjectResult>(
            await BuildController(ServiceWithContactEmail("admin@example.org"), dispatch: dispatch).PreviewCampaign("newsletter"));
        Assert.Contains("\"movies\":3", JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Admin - email templates
    // -------------------------------------------------------------------------

    [Fact]
    public void GetTemplates_ListsTheTwoBases_AndACloneRoundTrips()
    {
        var templates = TestTemplateStore.Create();
        var controller = BuildController(templates: templates);

        var listed = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(controller.GetTemplates()).Value);
        Assert.Contains("newsletter", listed, StringComparison.Ordinal);
        Assert.Contains("weekly-recap", listed, StringComparison.Ordinal);

        Assert.IsType<ObjectResult>(controller.CloneTemplate(new TemplateCloneRequest { BaseId = "newsletter", Slug = "holiday" }));
        Assert.Contains("newsletter__holiday", JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(controller.GetTemplates()).Value), StringComparison.Ordinal);
    }

    [Fact]
    public void GetTemplates_MarksTheSeriesBaseNotCloneable_AndRefusesItsClone()
    {
        var controller = BuildController(templates: TestTemplateStore.Create());

        var listed = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(controller.GetTemplates()).Value);

        Assert.Contains("{\"id\":\"series-episodes\",\"baseId\":\"series-episodes\",\"custom\":false,\"langs\":[\"en\",\"fr\"],\"cloneable\":false}", listed, StringComparison.Ordinal);
        Assert.Contains("{\"id\":\"newsletter\",\"baseId\":\"newsletter\",\"custom\":false,\"langs\":[\"en\",\"fr\"],\"cloneable\":true}", listed, StringComparison.Ordinal);
        Assert.IsType<BadRequestObjectResult>(controller.CloneTemplate(new TemplateCloneRequest { BaseId = "series-episodes", Slug = "x" }));
    }

    [Fact]
    public void PreviewTemplate_SeriesBase_RendersTheSampleModel()
    {
        var templates = TestTemplateStore.Create();
        var controller = BuildController(templates: templates);

        var result = controller.PreviewTemplate(new TemplatePreviewRequest { BaseId = "series-episodes", Content = templates.Get("series-episodes", "en") });

        var html = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Contains("Severance", html, StringComparison.Ordinal);
        Assert.Contains("8 new episodes (S03E01 to S03E08)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTemplate_UnknownId_Returns404()
        => Assert.IsType<NotFoundResult>(BuildController().GetTemplate("nope", "en"));

    [Fact]
    public void CloneTemplate_InvalidBase_Returns400()
    {
        var result = BuildController().CloneTemplate(new TemplateCloneRequest { BaseId = "nope", Slug = "x" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutTemplate_BaseTemplate_Returns403()
    {
        var result = BuildController().PutTemplate("newsletter", "en", new TemplateSaveRequest { Content = "<p>x</p>" });

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void PutTemplate_UnknownKey_Returns400_WithTheOffendingKey()
    {
        var templates = TestTemplateStore.Create();
        templates.Clone("newsletter", "x");
        var controller = BuildController(templates: templates);

        var bad = Assert.IsType<BadRequestObjectResult>(
            controller.PutTemplate("newsletter__x", "en", new TemplateSaveRequest { Content = "<p>{{heading}} {{mystery}}</p>" }));

        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("unknown-key", json, StringComparison.Ordinal);
        Assert.Contains("mystery", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PutTemplate_ScriptTag_Returns400()
    {
        var templates = TestTemplateStore.Create();
        templates.Clone("newsletter", "x");

        var bad = Assert.IsType<BadRequestObjectResult>(
            BuildController(templates: templates).PutTemplate("newsletter__x", "en", new TemplateSaveRequest { Content = "<script>alert(1)</script>" }));

        Assert.Contains("script", JsonSerializer.Serialize(bad.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void PutTemplate_ValidBody_Returns204_AndTheComposerUsesIt()
    {
        var templates = TestTemplateStore.Create();
        templates.Clone("newsletter", "x");
        var controller = BuildController(templates: templates);

        var result = controller.PutTemplate("newsletter__x", "fr", new TemplateSaveRequest { Content = "<p>{{heading}} EDITED</p>" });

        Assert.IsType<NoContentResult>(result);
        Assert.Contains("EDITED", templates.Get("newsletter__x", "fr"), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteTemplate_InUseByACampaign_Returns409()
    {
        var templates = TestTemplateStore.Create();
        templates.Clone("newsletter", "x");
        var campaigns = SeededCampaigns();
        campaigns.Campaigns[0].TemplateId = "newsletter__x";

        var result = BuildController(campaigns: campaigns, templates: templates).DeleteTemplate("newsletter__x");

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ConflictObjectResult>(result).StatusCode);
    }

    [Fact]
    public void DeleteTemplate_NotInUse_Returns204()
    {
        var templates = TestTemplateStore.Create();
        templates.Clone("newsletter", "x");

        Assert.IsType<NoContentResult>(BuildController(campaigns: SeededCampaigns(), templates: templates).DeleteTemplate("newsletter__x"));
    }

    [Fact]
    public void DeleteTemplate_UnknownId_Returns404()
    {
        var result = BuildController(campaigns: SeededCampaigns(), templates: TestTemplateStore.Create())
            .DeleteTemplate("newsletter__never-cloned");

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<NotFoundObjectResult>(result).StatusCode);
    }

    [Fact]
    public void PreviewTemplate_RendersSampleData()
    {
        var ok = Assert.IsType<OkObjectResult>(
            BuildController().PreviewTemplate(new TemplatePreviewRequest { BaseId = "newsletter", Content = "<h1>{{heading}}</h1>" }));

        Assert.Contains("Lili serveur", JsonSerializer.Serialize(ok.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewTemplate_WithAnOversizedBody_Returns400_WithoutRendering()
    {
        var result = BuildController().PreviewTemplate(new TemplatePreviewRequest
        {
            BaseId = "newsletter",
            Content = new string('x', 70 * 1024)
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("too-large", JsonSerializer.Serialize(bad.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void PutCampaign_TemplateIncompatibleWithType_Returns400()
    {
        var templates = TestTemplateStore.Create();
        templates.Clone("weekly-recap", "x");

        var result = BuildController(campaigns: SeededCampaigns(), templates: templates)
            .PutCampaign("newsletter", new CampaignUpdate { TemplateId = "weekly-recap__x" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutCampaign_CompatibleTemplate_Returns204_AndIsStored()
    {
        var templates = TestTemplateStore.Create();
        templates.Clone("newsletter", "x");
        var campaigns = SeededCampaigns();

        var result = BuildController(campaigns: campaigns, templates: templates)
            .PutCampaign("newsletter", new CampaignUpdate { TemplateId = "newsletter__x" });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("newsletter__x", campaigns.Campaigns[0].TemplateId);
    }

    [Fact]
    public void PutCampaign_EmptyTemplateId_ResetsToTheBase()
    {
        var campaigns = SeededCampaigns();
        campaigns.Campaigns[0].TemplateId = "newsletter__x";

        BuildController(campaigns: campaigns).PutCampaign("newsletter", new CampaignUpdate { TemplateId = string.Empty });

        Assert.Equal("newsletter", campaigns.Campaigns[0].TemplateId);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static MethodInfo Method(string name) => typeof(EasyNotifController).GetMethod(name)!;

    private static EasyNotifController BuildController(
        Mock<IPreferenceService>? service = null,
        IConfigAccessor? config = null,
        FakeSecretStore? secrets = null,
        Mock<IAuthorizationContext>? auth = null,
        Mock<IEmailSender>? emailSender = null,
        Mock<IQuotaGuard>? quota = null,
        SendCooldown? sendCooldown = null,
        Mock<ISendLog>? sendLog = null,
        Mock<IManualEmailService>? manualEmail = null,
        ICampaignStore? campaigns = null,
        Mock<IDispatchService>? dispatch = null,
        ITemplateStore? templates = null,
        Mock<IFileTransformationDetector>? fileTransformation = null,
        FakeEasyNotifLog? easyNotifLog = null,
        Mock<IManualSendQueue>? manualQueue = null,
        Mock<IFollowedSeriesService>? followedSeries = null,
        IDrainStatus? drains = null)
    {
        var effectiveConfig = config ?? new FakeConfig();
        var cfgValues = effectiveConfig.Get();
        var effectiveSecrets = secrets ?? new FakeSecretStore(
            cfgValues.ResendApiKey,
            cfgValues.WebhookSigningSecret,
            cfgValues.UnsubscribeSecret);

        Mock<IQuotaGuard> quotaMock;
        if (quota is null)
        {
            quotaMock = new Mock<IQuotaGuard>();
            quotaMock.Setup(q => q.Snapshot()).Returns(new QuotaSnapshot(0, 0, 3000, 100, false, false));
        }
        else
        {
            quotaMock = quota;
        }

        var controller = new EasyNotifController(
            (service ?? new Mock<IPreferenceService>()).Object,
            (followedSeries ?? new Mock<IFollowedSeriesService>()).Object,
            effectiveConfig,
            effectiveSecrets,
            (auth ?? AuthReturning(DefaultUserId)).Object,
            (emailSender ?? new Mock<IEmailSender>()).Object,
            quotaMock.Object,
            sendCooldown ?? new SendCooldown(),
            (sendLog ?? new Mock<ISendLog>()).Object,
            (manualEmail ?? new Mock<IManualEmailService>()).Object,
            (manualQueue ?? new Mock<IManualSendQueue>()).Object,
            campaigns ?? new FakeCampaignStore(),
            (dispatch ?? new Mock<IDispatchService>()).Object,
            templates ?? TestTemplateStore.Create(),
            (fileTransformation ?? new Mock<IFileTransformationDetector>()).Object,
            drains ?? Mock.Of<IDrainStatus>(d => d.Snapshot() == new Dictionary<string, DrainHealthSnapshot>()),
            easyNotifLog ?? new FakeEasyNotifLog(),
            NullLogger<EasyNotifController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static Mock<IAuthorizationContext> AuthReturning(Guid userId)
    {
        var mock = new Mock<IAuthorizationContext>();
        var user = new User("test", "Default", "Default") { Id = userId };
        mock.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = user });
        return mock;
    }

    private static ActionResult InvokeWrap(EasyNotifController controller, Func<ActionResult> action)
    {
        var wrap = typeof(EasyNotifController).GetMethod("Wrap", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            return (ActionResult)wrap.Invoke(controller, [action])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private sealed class FakeConfig : IConfigAccessor
    {
        public FakeConfig(PluginConfiguration? config = null) => Config = config ?? new PluginConfiguration();

        public PluginConfiguration Config { get; }

        public int SaveCount { get; private set; }

        public PluginConfiguration Get() => Config;

        public void Save() => SaveCount++;
    }
}
