using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Controllers;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Controllers;

/// <summary>
/// Covers <see cref="UnsubscribeController"/>: an invalid token is 404, GET renders the page without
/// changing anything, POST toggles the preference and logs a masked action, a one-click body returns
/// a bare 200 while a browser form returns the confirmation page, and <c>all</c> toggles both
/// categories.
/// </summary>
public sealed class UnsubscribeControllerTests
{
    private const string Secret = "unsub-secret-0123456789";
    private static readonly Guid User = Guid.NewGuid();

    private static (UnsubscribeController Controller, Mock<IPreferenceService> Prefs, FakeEasyNotifLog Log) Build(
        bool newsOptIn = true, bool recapOptIn = true)
    {
        var prefs = new Mock<IPreferenceService>();
        prefs.Setup(p => p.GetOrCreate(User)).Returns(new UserPreference
        {
            UserId = User,
            Categories = { [EmailCategory.News] = newsOptIn, [EmailCategory.Recap] = recapOptIn }
        });
        var log = new FakeEasyNotifLog();
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(User)).Returns(new User("alice", "Default", "Default") { Id = User });
        var controller = new UnsubscribeController(
            new FakeSecretStore(unsubscribeSecret: Secret),
            prefs.Object,
            users.Object,
            log,
            NullLogger<UnsubscribeController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, prefs, log);
    }

    private static string Token(string category) => UnsubscribeToken.Create(Secret, User, category);

    private static void SetForm(UnsubscribeController controller, Dictionary<string, string> fields)
    {
        var request = controller.ControllerContext.HttpContext.Request;
        request.ContentType = "application/x-www-form-urlencoded";
        request.Form = new FormCollection(fields.ToDictionary(
            kv => kv.Key,
            kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));
    }

    [Fact]
    public void Get_ValidToken_RendersHtml_WithoutChangingAnything()
    {
        var (controller, prefs, _) = Build();

        var result = Assert.IsType<ContentResult>(controller.Get(Token("news")));

        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Contains("<form method=\"post\">", result.Content!, StringComparison.Ordinal);
        prefs.Verify(p => p.SetCategories(It.IsAny<Guid>(), It.IsAny<IReadOnlyDictionary<EmailCategory, bool>>(), It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public void Get_InvalidToken_Returns404()
        => Assert.IsType<NotFoundResult>(Build().Controller.Get("garbage.token"));

    [Fact]
    public void Post_InvalidToken_Returns404()
        => Assert.IsType<NotFoundResult>(Build().Controller.Post("garbage.token"));

    [Fact]
    public void Post_Default_Unsubscribes_LogsAMaskedAction_ReturnsThePage()
    {
        var (controller, prefs, log) = Build();

        var result = controller.Post(Token("news"));

        Assert.IsType<ContentResult>(result);
        prefs.Verify(p => p.SetCategories(User, It.Is<IReadOnlyDictionary<EmailCategory, bool>>(
            m => m.Count == 1 && m[EmailCategory.News] == false), null), Times.Once);
        var entry = Assert.Single(log.Entries, e => e.EventType == "unsubscribe.action");
        Assert.Equal("Info", entry.Level);
        Assert.Equal(false, entry.Fields!["resubscribe"]);
    }

    [Fact]
    public void Post_Resubscribe_SetsTheCategoryTrue()
    {
        var (controller, prefs, _) = Build(newsOptIn: false);
        SetForm(controller, new() { ["resubscribe"] = "true" });

        controller.Post(Token("news"));

        prefs.Verify(p => p.SetCategories(User, It.Is<IReadOnlyDictionary<EmailCategory, bool>>(
            m => m[EmailCategory.News]), null), Times.Once);
    }

    [Fact]
    public void Post_OneClickBody_ReturnsBareOk()
    {
        var (controller, _, _) = Build();
        SetForm(controller, new() { ["List-Unsubscribe"] = "One-Click" });

        Assert.IsType<OkResult>(controller.Post(Token("recap")));
    }

    [Fact]
    public void Post_StorageFailure_Returns503_NotAnUnhandledError()
    {
        var (controller, prefs, _) = Build();
        prefs.Setup(p => p.SetCategories(It.IsAny<Guid>(), It.IsAny<IReadOnlyDictionary<EmailCategory, bool>>(), It.IsAny<bool?>()))
            .Throws(new UnauthorizedAccessException("preferences.json is locked"));

        var result = controller.Post(Token("news"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public void Post_CategoryAll_TurnsOffBothCategoriesAndAnnouncements()
    {
        var (controller, prefs, _) = Build();

        controller.Post(Token("all"));

        prefs.Verify(p => p.SetCategories(User, It.Is<IReadOnlyDictionary<EmailCategory, bool>>(
            m => m.Count == 2 && !m[EmailCategory.News] && !m[EmailCategory.Recap]), true), Times.Once);
    }

    [Fact]
    public void Post_CategoryAll_Resubscribe_TurnsAnnouncementsBackOn()
    {
        var (controller, prefs, _) = Build(newsOptIn: false, recapOptIn: false);
        SetForm(controller, new() { ["resubscribe"] = "true" });

        controller.Post(Token("all"));

        prefs.Verify(p => p.SetCategories(User, It.IsAny<IReadOnlyDictionary<EmailCategory, bool>>(), false), Times.Once);
    }

    [Fact]
    public void Post_TokenOfADeletedUser_Returns404_AndTouchesNoPreference()
    {
        var (controller, prefs, _) = Build();
        var deleted = UnsubscribeToken.Create(Secret, Guid.NewGuid(), "all");

        Assert.IsType<NotFoundResult>(controller.Post(deleted));
        Assert.IsType<NotFoundResult>(controller.Get(deleted));
        prefs.VerifyNoOtherCalls();
    }

    [Fact]
    public void UnknownCategory_Returns404()
    {
        var token = UnsubscribeToken.Create(Secret, User, "weird");

        Assert.IsType<NotFoundResult>(Build().Controller.Get(token));
    }

    [Fact]
    public void PickLang_QueryWins_ThenAcceptLanguage()
    {
        var (controller, _, _) = Build();
        controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?lang=fr");

        var result = Assert.IsType<ContentResult>(controller.Get(Token("news")));

        Assert.Contains("lang=\"fr\"", result.Content!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(UnsubscribeController.Get))]
    [InlineData(nameof(UnsubscribeController.Post))]
    public void Endpoints_AreAnonymous(string methodName)
        => Assert.NotNull(typeof(UnsubscribeController).GetMethod(methodName)!.GetCustomAttribute<AllowAnonymousAttribute>());
}
