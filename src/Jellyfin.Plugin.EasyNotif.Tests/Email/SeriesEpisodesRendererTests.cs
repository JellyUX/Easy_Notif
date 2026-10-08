using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="SeriesEpisodesRenderer"/> through the embedded <c>series-episodes</c> template:
/// labels (specials, multi-episode files, dated and fallback episodes), the summary above five
/// episodes, posters, series links, the per-series and global unsubscribe links, French and English,
/// and HTML escaping.
/// </summary>
public sealed class SeriesEpisodesRendererTests
{
    private readonly TemplateStore _templates = TestTemplateStore.Create();

    [Theory]
    [InlineData(2, 5, null, true, "S02E05")]
    [InlineData(2, 5, 6, true, "S02E05-E06")]
    [InlineData(0, 3, null, true, "Spécial 3")]
    [InlineData(0, 3, null, false, "Special 3")]
    [InlineData(null, 7, null, true, "E07")]
    [InlineData(null, null, null, true, "Nouvel épisode")]
    [InlineData(null, null, null, false, "New episode")]
    public void Label_CoversEveryNumberingCase(int? season, int? number, int? end, bool french, string expected)
        => Assert.Equal(expected, SeriesEpisodesRenderer.Label(new SeriesEpisodeLine(season, number, end, null), french));

    [Fact]
    public void Label_OfADatedEpisode_IsItsPremiereDate()
        => Assert.Equal("2026-06-14", SeriesEpisodesRenderer.Label(new SeriesEpisodeLine(null, null, null, new DateTime(2026, 6, 14)), true));

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void EmbeddedTemplate_PassesValidation(string lang)
    {
        var template = _templates.Get(SeriesEpisodesRenderer.TemplateId, lang);

        Assert.Contains($"lang=\"{lang}\"", template, StringComparison.Ordinal);
        Assert.True(_templates.Validate(SeriesEpisodesRenderer.TemplateId, template).Ok);
    }

    [Fact]
    public void Render_SummarisesASeriesAboveFiveEpisodes_InFrenchAndEnglish()
    {
        var digest = new SeriesEpisodesDigest(
        [
            new SeriesEpisodesGroup(Guid.NewGuid(), "Andor", [.. Enumerable.Range(1, 6).Select(n => new SeriesEpisodeLine(1, n, null, null))]),
            new SeriesEpisodesGroup(Guid.NewGuid(), "Severance", [.. Enumerable.Range(1, 5).Select(n => new SeriesEpisodeLine(2, n, null, null))])
        ]);

        var fr = Render(digest, french: true);
        var en = Render(digest, french: false);

        Assert.Equal("Nouveaux épisodes de 2 séries", fr.Subject);
        Assert.Contains("Nouveaux épisodes sur Lili", fr.Html, StringComparison.Ordinal);
        Assert.Contains("11 nouveaux épisodes", fr.Html, StringComparison.Ordinal);
        Assert.Contains("6 nouveaux épisodes (S01E01 à S01E06)", fr.Html, StringComparison.Ordinal);
        Assert.Contains("6 nouveaux épisodes (S01E01 à S01E06)", fr.Text, StringComparison.Ordinal);
        Assert.Contains(">S02E05</div>", fr.Html, StringComparison.Ordinal);
        Assert.Contains("- S02E05", fr.Text, StringComparison.Ordinal);
        Assert.Equal("New episodes of 2 series", en.Subject);
        Assert.Contains("New episodes on Lili", en.Html, StringComparison.Ordinal);
        Assert.Contains("6 new episodes (S01E01 to S01E06)", en.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_LabelsSpecials_InFrenchAndEnglish()
    {
        var digest = new SeriesEpisodesDigest([new SeriesEpisodesGroup(Guid.NewGuid(), "Slow Horses", [new SeriesEpisodeLine(0, 1, null, null)])]);

        Assert.Contains(">Spécial 1</div>", Render(digest, french: true).Html, StringComparison.Ordinal);
        Assert.Contains(">Special 1</div>", Render(digest, french: false).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithLinks_LinksTheSeries_ShowsThePoster_AndBothUnsubscribeLinks()
    {
        var digest = new SeriesEpisodesDigest(
            [new SeriesEpisodesGroup(
                Guid.NewGuid(),
                "Severance",
                [new SeriesEpisodeLine(2, 5, null, null)],
                "https://media.example.org/EasyNotif/u/series-token?lang=fr",
                "https://media.example.org/Items/abc/Images/Primary?maxWidth=300&quality=85",
                "https://media.example.org/web/#/details?id=abc&serverId=sys")],
            "https://media.example.org/EasyNotif/u/all-token?lang=fr");

        var fr = Render(digest, french: true);
        var en = Render(digest, french: false);

        Assert.Contains("<a href=\"https://media.example.org/web/#/details?id=abc&amp;serverId=sys\"", fr.Html, StringComparison.Ordinal);
        Assert.Contains("<img src=\"https://media.example.org/Items/abc/Images/Primary?maxWidth=300&amp;quality=85\"", fr.Html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://media.example.org/EasyNotif/u/series-token?lang=fr\" style=\"color:#6b7280;\">Ne plus suivre cette série</a>", fr.Html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://media.example.org/EasyNotif/u/all-token?lang=fr\" style=\"color:#6b7280;\">Ne plus recevoir les mails de nouveaux épisodes</a>", fr.Html, StringComparison.Ordinal);
        Assert.Contains("Voir la série: https://media.example.org/web/#/details?id=abc&serverId=sys", fr.Text, StringComparison.Ordinal);
        Assert.Contains("Ne plus suivre cette série: https://media.example.org/EasyNotif/u/series-token?lang=fr", fr.Text, StringComparison.Ordinal);
        Assert.Contains(">Stop following this series</a>", en.Html, StringComparison.Ordinal);
        Assert.Contains(">Stop all new-episode emails</a>", en.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_WithoutLinks_LeavesTheTitlePlain_AndShowsNoUnsubscribeLink()
    {
        var digest = new SeriesEpisodesDigest([new SeriesEpisodesGroup(Guid.NewGuid(), "Severance", [new SeriesEpisodeLine(2, 5, null, null)], PosterUrl: "cid:poster-1")]);

        var (_, html, text) = Render(digest, french: true);

        Assert.Contains("<img src=\"cid:poster-1\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.Contains(">Severance</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OneSeries_NamesItInTheSubject_AndEscapesHtml()
    {
        var digest = new SeriesEpisodesDigest([new SeriesEpisodesGroup(Guid.NewGuid(), "Law & <Order>", [new SeriesEpisodeLine(1, 1, null, null)])]);

        var (subject, html, _) = Render(digest, french: true);

        Assert.Equal("Nouveaux épisodes : Law & <Order>", subject);
        Assert.Contains("Law &amp; &lt;Order&gt;", html, StringComparison.Ordinal);
        Assert.Contains("1 nouvel épisode", html, StringComparison.Ordinal);
    }

    private (string Subject, string Html, string Text) Render(SeriesEpisodesDigest digest, bool french)
        => SeriesEpisodesRenderer.Render(digest, french, _templates.Get(SeriesEpisodesRenderer.TemplateId, french ? "fr" : "en"), "Lili");
}
