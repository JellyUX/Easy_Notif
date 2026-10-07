using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="NewsletterComposer"/>: the HTML carries titles and deep links, the text part
/// is plain and non-empty, the subject is localised, an empty digest renders the "nothing new"
/// body without sections, and a missing deep link drops the anchor.
/// </summary>
public sealed class NewsletterComposerTests
{
    private static readonly NewsletterComposer Composer = new(TestTemplateStore.Create());

    private static EmailContent Compose(Campaign campaign, NewsletterDigest digest, string serverName)
        => Composer.Compose(campaign, digest, serverName, "newsletter");

    private static Campaign Campaign(string lang = "fr") => new()
    {
        Id = "newsletter",
        Type = CampaignType.Newsletter,
        Category = EmailCategory.News,
        Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Friday, new TimeOnly(9, 0)),
        MailLanguage = lang
    };

    private static NewsletterDigest OneOfEach(string? detailUrl = "https://media.example.org/web/#/details?id=1")
        => new(
            [new DigestMovie(Guid.NewGuid(), "Sicario", 2015, "A tense thriller", ["Crime", "Drama"], "R", "https://media.example.org/p.jpg", detailUrl)],
            [new DigestSeries(Guid.NewGuid(), "Severance", 3, [1], "Work and life, split", null, detailUrl)]);

    [Fact]
    public void NonEmptyDigest_HtmlHasTitlesAndLink_TextIsPlain()
    {
        var content = Compose(Campaign(), OneOfEach(), "Home");

        Assert.Contains("Sicario", content.Html!, StringComparison.Ordinal);
        Assert.Contains("Severance", content.Html!, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://media.example.org/web/#/details?id=1\"", content.Html!, StringComparison.Ordinal);
        Assert.Contains("3 nouveaux épisodes", content.Html!, StringComparison.Ordinal);

        Assert.False(string.IsNullOrWhiteSpace(content.Text));
        Assert.Contains("Sicario", content.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("<", content.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Subject_IsLocalised()
    {
        Assert.StartsWith("Nouveautés médiathèque", Compose(Campaign("fr"), OneOfEach(), "Home").Subject);
        Assert.StartsWith("New in your library", Compose(Campaign("en"), OneOfEach(), "Home").Subject);
    }

    [Fact]
    public void EmptyDigest_RendersNothingNew_NoSections()
    {
        var content = Compose(Campaign("en"), new NewsletterDigest([], []), "Home");

        Assert.Contains("Nothing new was added this week", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain(">Movies<", content.Html!, StringComparison.Ordinal);
        Assert.Contains("nothing new this week", content.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDetailUrl_DropsTheAnchor_KeepsTheTitle()
    {
        var content = Compose(Campaign("en"), OneOfEach(detailUrl: null), "Home");

        Assert.Contains("Sicario", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("<a href=", content.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public void InlinePosters_FlowThroughToTheEmailContentAttachments()
    {
        var poster = new EmailAttachment { FileName = "p.jpg", Content = [1], ContentType = "image/jpeg", ContentId = "poster-x" };
        var digest = new NewsletterDigest(
            [new DigestMovie(Guid.NewGuid(), "Offline", 2020, null, [], null, "cid:poster-x", null)],
            [],
            [poster]);

        var content = Compose(Campaign("en"), digest, "Home");

        Assert.NotNull(content.Attachments);
        Assert.Same(poster, Assert.Single(content.Attachments!));
        Assert.Contains("cid:poster-x", content.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public void NoInlinePosters_LeavesAttachmentsNull()
        => Assert.Null(Compose(Campaign("en"), OneOfEach(), "Home").Attachments);

    [Fact]
    public void UnsubscribeUrl_WhenGiven_AppearsInTheHtmlAndText()
    {
        var content = Composer.Compose(Campaign("en"), OneOfEach(), "Home", "newsletter", "https://media.example.org/EasyNotif/u/tok?lang=en");

        Assert.Contains("<a href=\"https://media.example.org/EasyNotif/u/tok?lang=en\"", content.Html!, StringComparison.Ordinal);
        Assert.Contains("Unsubscribe: https://media.example.org/EasyNotif/u/tok?lang=en", content.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsubscribeUrl_WhenNull_AddsNoFooterLink()
    {
        var content = Compose(Campaign("en"), OneOfEach(), "Home");

        Assert.DoesNotContain("/EasyNotif/u/", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsubscribe:", content.Text!, StringComparison.Ordinal);
    }

    // 500 items with long fields and remote posters, the worst realistic case for the HTML size.
    private static NewsletterDigest FiveHundredItems()
    {
        var overview = new string('x', 300);
        var movies = Enumerable.Range(0, 300)
            .Select(i => new DigestMovie(Guid.NewGuid(), $"Movie {i:D3}", 2020, overview, ["Drama", "Thriller"], "PG-13", $"https://media.example.org/Items/{i}/Images/Primary?maxWidth=300&quality=85", $"https://media.example.org/web/#/details?id={i}&serverId=srv"))
            .ToList();
        var series = Enumerable.Range(0, 200)
            .Select(i => new DigestSeries(Guid.NewGuid(), $"Series {i:D3}", 1, [1], overview, $"https://media.example.org/Items/s{i}/Images/Primary?maxWidth=300&quality=85", $"https://media.example.org/web/#/details?id=s{i}&serverId=srv"))
            .ToList();
        return new NewsletterDigest(movies, series).Capped(30, 30);
    }

    [Theory]
    [InlineData("fr", "et 270 autres films", "et 170 autres séries")]
    [InlineData("en", "and 270 more movies", "and 170 more series")]
    public void FiveHundredItems_ListThirtyOfEach_WithMoreLines_AndStayUnderGmailsClip(string lang, string moreMovies, string moreSeries)
    {
        var content = Composer.Compose(Campaign(lang), FiveHundredItems(), "Home", "newsletter", libraryUrl: "https://media.example.org/web/");

        var html = content.Html!;
        Assert.Contains("Movie 029", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Movie 030", html, StringComparison.Ordinal);
        Assert.Contains("Series 029", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Series 030", html, StringComparison.Ordinal);
        Assert.Contains($">{moreMovies}</a>", html, StringComparison.Ordinal);
        Assert.Contains($">{moreSeries}</a>", html, StringComparison.Ordinal);
        Assert.Contains(moreMovies + "  https://media.example.org/web/", content.Text!, StringComparison.Ordinal);
        Assert.Contains("500", content.Subject, StringComparison.Ordinal); // the subject still counts everything
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(html) < 102 * 1024, $"HTML is {System.Text.Encoding.UTF8.GetByteCount(html)} bytes");
    }

    [Fact]
    public void MoreLine_WithoutLibraryUrl_IsPlainText()
    {
        var html = Composer.Compose(Campaign(), FiveHundredItems(), "Home", "newsletter").Html!;

        Assert.Contains(">et 270 autres films</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/web/\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void UncappedDigest_HasNoMoreLine()
        => Assert.DoesNotContain("et 0", Compose(Campaign(), OneOfEach(), "Home").Html!, StringComparison.Ordinal);
}
