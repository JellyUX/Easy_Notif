using Jellyfin.Plugin.EasyNotif.Email;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="SeriesEpisodesRenderer"/>, the provisional rendering of DEC-33: labels (specials,
/// multi-episode files, dated and fallback episodes), the summary above five episodes, French and
/// English, and HTML escaping.
/// </summary>
public sealed class SeriesEpisodesRendererTests
{
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

    [Fact]
    public void Render_SummarisesASeriesAboveFiveEpisodes_InFrenchAndEnglish()
    {
        var digest = new SeriesEpisodesDigest(
        [
            new SeriesEpisodesGroup(Guid.NewGuid(), "Andor", [.. Enumerable.Range(1, 6).Select(n => new SeriesEpisodeLine(1, n, null, null))]),
            new SeriesEpisodesGroup(Guid.NewGuid(), "Severance", [.. Enumerable.Range(1, 5).Select(n => new SeriesEpisodeLine(2, n, null, null))])
        ]);

        var fr = SeriesEpisodesRenderer.Render(digest, french: true);
        var en = SeriesEpisodesRenderer.Render(digest, french: false);

        Assert.Equal("Nouveaux épisodes de 2 séries", fr.Subject);
        Assert.Contains("6 nouveaux épisodes (S01E01 à S01E06)", fr.Text, StringComparison.Ordinal);
        Assert.Contains("- S02E05", fr.Text, StringComparison.Ordinal);
        Assert.Equal("New episodes of 2 series", en.Subject);
        Assert.Contains("6 new episodes (S01E01 to S01E06)", en.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OneSeries_NamesItInTheSubject_AndEscapesHtml()
    {
        var digest = new SeriesEpisodesDigest([new SeriesEpisodesGroup(Guid.NewGuid(), "Law & <Order>", [new SeriesEpisodeLine(1, 1, null, null)])]);

        var (subject, html, _) = SeriesEpisodesRenderer.Render(digest, french: true);

        Assert.Equal("Nouveaux épisodes : Law & <Order>", subject);
        Assert.Contains("Law &amp; &lt;Order&gt;", html, StringComparison.Ordinal);
    }
}
