using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Media;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="NewsletterDigest.For"/>, the per-recipient narrowing behind access filtering
/// (SEC-01): forbidden movies go, a series is recounted from its visible episodes or dropped, and an
/// inline poster leaves with the item that referenced it.
/// </summary>
public sealed class NewsletterDigestTests
{
    private static readonly Guid Dune = Guid.NewGuid();
    private static readonly Guid Forbidden = Guid.NewGuid();
    private static readonly Guid S1E1 = Guid.NewGuid();
    private static readonly Guid S2E1 = Guid.NewGuid();
    private static readonly Guid S2E2 = Guid.NewGuid();
    private static readonly Guid AdultEpisode = Guid.NewGuid();

    private static EmailAttachment Poster(string cid) => new() { FileName = cid + ".jpg", Content = [1], ContentId = cid };

    private static NewsletterDigest Full() => new(
        [
            new DigestMovie(Dune, "Dune", 2021, null, [], null, "cid:p-dune", null),
            new DigestMovie(Forbidden, "Forbidden", 2024, null, [], "R", "cid:p-forbidden", null)
        ],
        [
            new DigestSeries(Guid.NewGuid(), "Mixed", 3, [1, 2], null, "cid:p-mixed", null,
                [new DigestEpisode(S1E1, 1), new DigestEpisode(S2E1, 2), new DigestEpisode(S2E2, 2)]),
            new DigestSeries(Guid.NewGuid(), "Severance", 1, [1], null, "cid:p-severance", null,
                [new DigestEpisode(AdultEpisode, 1)])
        ],
        [Poster("p-dune"), Poster("p-forbidden"), Poster("p-mixed"), Poster("p-severance")]);

    [Fact]
    public void ForbiddenMovie_IsDropped_WithItsPoster()
    {
        var scoped = Full().For(new HashSet<Guid> { Dune, S1E1, S2E1, S2E2, AdultEpisode });

        Assert.Equal(["Dune"], scoped.Movies.Select(m => m.Title));
        Assert.DoesNotContain(scoped.Posters, p => p.ContentId == "p-forbidden");
        Assert.Equal(3, scoped.Posters.Count);
    }

    [Fact]
    public void PartlyVisibleSeries_IsRecounted_AndSeasonsFollow()
    {
        var scoped = Full().For(new HashSet<Guid> { S1E1 });

        var mixed = Assert.Single(scoped.Series);
        Assert.Equal("Mixed", mixed.Title);
        Assert.Equal(1, mixed.NewEpisodeCount);
        Assert.Equal([1], mixed.Seasons);
        Assert.Equal(1, scoped.TotalItems);
        Assert.Equal(["p-mixed"], scoped.Posters.Select(p => p.ContentId));
    }

    [Fact]
    public void NothingVisible_GivesAnEmptyDigest()
    {
        var scoped = Full().For(new HashSet<Guid>());

        Assert.True(scoped.IsEmpty);
        Assert.Empty(scoped.Posters);
    }

    [Fact]
    public void SeriesWithoutEpisodeList_FailsClosed()
    {
        var digest = new NewsletterDigest([], [new DigestSeries(Guid.NewGuid(), "Legacy", 2, [1], null, null, null)]);

        Assert.True(digest.For(new HashSet<Guid> { Guid.NewGuid() }).IsEmpty);
    }

    [Fact]
    public void ItemIds_CoverMoviesAndEpisodes_NotSeries()
        => Assert.Equal(
            new[] { Dune, Forbidden, S1E1, S2E1, S2E2, AdultEpisode }.Order(),
            Full().ItemIds.Order());

    [Fact]
    public void Capped_KeepsTheFirstEntries_CountsTheRest_AndDropsTheirPosters()
    {
        var movies = Enumerable.Range(0, 300)
            .Select(i => new DigestMovie(Guid.NewGuid(), $"M{i}", null, null, [], null, $"cid:m{i}", null))
            .ToList();
        var series = Enumerable.Range(0, 200)
            .Select(i => new DigestSeries(Guid.NewGuid(), $"S{i}", 3, [1], null, $"cid:s{i}", null))
            .ToList();
        var posters = movies.Select(m => Poster(m.PosterUrl![4..])).Concat(series.Select(s => Poster(s.PosterUrl![4..]))).ToList();
        var digest = new NewsletterDigest(movies, series, posters) { Keys = ["k"] };

        var capped = digest.Capped(30, 30);

        Assert.Equal(movies.Take(30), capped.Movies);
        Assert.Equal(series.Take(30), capped.Series);
        Assert.Equal(270, capped.MoreMovies);
        Assert.Equal(170, capped.MoreSeries);
        Assert.Equal(digest.TotalItems, capped.TotalItems); // 300 movies + 600 episodes, still counted
        Assert.Equal(60, capped.Posters.Count);
        Assert.Equal(["k"], capped.Keys);
    }

    [Fact]
    public void Capped_UnderTheCap_IsUnchanged()
    {
        var digest = Full();

        Assert.Same(digest, digest.Capped(30, 30));
        Assert.Equal(0, digest.MoreMovies);
    }
}
