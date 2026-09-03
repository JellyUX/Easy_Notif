using System.Reflection;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Controllers;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
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
    public void GetStrings_ForAnUnknownLanguage_Returns404()
    {
        Assert.IsType<NotFoundResult>(BuildController().GetStrings("de"));
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
    // Helpers
    // -------------------------------------------------------------------------

    private static MethodInfo Method(string name) => typeof(EasyNotifController).GetMethod(name)!;

    private static EasyNotifController BuildController(
        Mock<IPreferenceService>? service = null,
        IConfigAccessor? config = null,
        Mock<IAuthorizationContext>? auth = null) =>
        new(
            (service ?? new Mock<IPreferenceService>()).Object,
            config ?? new FakeConfig(),
            (auth ?? AuthReturning(DefaultUserId)).Object,
            NullLogger<EasyNotifController>.Instance);

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
