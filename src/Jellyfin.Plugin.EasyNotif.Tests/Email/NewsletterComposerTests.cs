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
        Assert.Contains("3 nouveaux episodes", content.Html!, StringComparison.Ordinal);

        Assert.False(string.IsNullOrWhiteSpace(content.Text));
        Assert.Contains("Sicario", content.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("<", content.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void Subject_IsLocalised()
    {
        Assert.StartsWith("Nouveautes mediatheque", Compose(Campaign("fr"), OneOfEach(), "Home").Subject);
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
}
