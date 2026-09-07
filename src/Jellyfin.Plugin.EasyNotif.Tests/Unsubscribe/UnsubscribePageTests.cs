using Jellyfin.Plugin.EasyNotif.Unsubscribe;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Unsubscribe;

/// <summary>
/// Covers <see cref="UnsubscribePage.Build"/>: the category label and language are reflected, the
/// form posts the opposite of the current subscription state, a done flag adds a confirmation line,
/// and the markup carries no em dash or hex colour literal.
/// </summary>
public sealed class UnsubscribePageTests
{
    private static readonly string EmDash = char.ConvertFromUtf32(0x2014);

    [Fact]
    public void Subscribed_ShowsAnUnsubscribeButton_ThatPostsResubscribeFalse()
    {
        var html = UnsubscribePage.Build("fr", "recap", subscribed: true, done: false);

        Assert.Contains("lang=\"fr\"", html, StringComparison.Ordinal);
        Assert.Contains("Résumé de la semaine", html, StringComparison.Ordinal);
        Assert.Contains("name=\"resubscribe\" value=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("Me désabonner", html, StringComparison.Ordinal);
        Assert.DoesNotContain("C'est fait", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsubscribed_ShowsAResubscribeButton_ThatPostsResubscribeTrue()
    {
        var html = UnsubscribePage.Build("en", "news", subscribed: false, done: true);

        Assert.Contains("New media", html, StringComparison.Ordinal);
        Assert.Contains("name=\"resubscribe\" value=\"true\"", html, StringComparison.Ordinal);
        Assert.Contains("Resubscribe", html, StringComparison.Ordinal);
        Assert.Contains("Done.", html, StringComparison.Ordinal);
        Assert.Contains("no longer receive", html, StringComparison.Ordinal);
    }

    [Fact]
    public void All_UsesTheGenericLabel()
        => Assert.Contains("all Easy Notif emails", UnsubscribePage.Build("en", "all", true, false), StringComparison.Ordinal);

    [Fact]
    public void Markup_HasNoEmDashAndNoHexColourLiteral()
    {
        var html = UnsubscribePage.Build("fr", "news", true, true) + UnsubscribePage.Build("en", "all", false, false);

        Assert.DoesNotContain(EmDash, html, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?<![&])#[0-9a-fA-F]{3,8}\\b", html); // system colour keywords only, no hex
    }
}
