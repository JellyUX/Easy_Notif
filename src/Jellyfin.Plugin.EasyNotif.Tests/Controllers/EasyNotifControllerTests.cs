using System.Reflection;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Controllers;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Inject;
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

        Assert.Equal(5, meActions.Count);
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
        var config = new FakeConfig(new PluginConfiguration { ResendApiKey = "re_old" });
        var controller = BuildController(config: config);

        controller.PutSettings(new SettingsUpdate { ResendApiKey = " re_new " });

        Assert.Equal("re_new", config.Config.ResendApiKey);
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
        FakeEasyNotifLog? easyNotifLog = null)
    {
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
            config ?? new FakeConfig(),
            (auth ?? AuthReturning(DefaultUserId)).Object,
            (emailSender ?? new Mock<IEmailSender>()).Object,
            quotaMock.Object,
            sendCooldown ?? new SendCooldown(),
            (sendLog ?? new Mock<ISendLog>()).Object,
            (manualEmail ?? new Mock<IManualEmailService>()).Object,
            campaigns ?? new FakeCampaignStore(),
            (dispatch ?? new Mock<IDispatchService>()).Object,
            templates ?? TestTemplateStore.Create(),
            (fileTransformation ?? new Mock<IFileTransformationDetector>()).Object,
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
