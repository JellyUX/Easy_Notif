using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="NewsletterDigestService"/>: movies pass through, episodes group by series with
/// deduplicated seasons, the query is bounded, poster and deep-link URLs are built only when a
/// public server URL (and, for posters, an image) is present, and the library is only ever read.
/// </summary>
public sealed class NewsletterDigestServiceTests
{
    private static readonly Guid SeriesAId = Guid.NewGuid();
    private static readonly Guid SeriesBId = Guid.NewGuid();

    private sealed class Harness
    {
        public Mock<ILibraryManager> Library { get; } = new();

        public FakeConfigAccessor Config { get; }

        public List<BaseItem> MainItems { get; } = [];

        public List<BaseItem> SeriesItems { get; } = [];

        public NewsletterDigestService Service { get; }

        public InternalItemsQuery? CapturedMainQuery { get; private set; }

        public Harness(string publicUrl = "https://media.example.org")
        {
            Config = new FakeConfigAccessor(new PluginConfiguration { PublicServerUrl = publicUrl });
            Library
                .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns<InternalItemsQuery>(q =>
                {
                    if (q.ItemIds is { Length: > 0 })
                    {
                        return SeriesItems;
                    }

                    CapturedMainQuery = q;
                    return MainItems;
                });
            Service = new NewsletterDigestService(Library.Object, Config, new ServerLinkContext("srv-1", "Home"), new FakeEasyNotifLog());
        }
    }

    private static Movie Movie(string name, bool withImage = false, int? year = 2026, DateTime? created = null)
    {
        var m = new Movie { Id = Guid.NewGuid(), Name = name, ProductionYear = year, Overview = "About " + name, Genres = ["Drama"], DateCreated = created ?? DateTime.UtcNow };
        if (withImage)
        {
            m.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "/x.jpg" }];
        }

        return m;
    }

    private static Episode Episode(Guid seriesId, string seriesName, int season, DateTime created)
        => new()
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            SeriesName = seriesName,
            ParentIndexNumber = season,
            DateCreated = created
        };

    [Fact]
    public void GroupsEpisodesBySeries_MoviesPassThrough_SeasonsDeduplicated()
    {
        var h = new Harness();
        h.MainItems.Add(Movie("Arrival"));
        h.MainItems.Add(Movie("Dune"));
        var t0 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        h.MainItems.Add(Episode(SeriesAId, "Series A", 1, t0));
        h.MainItems.Add(Episode(SeriesAId, "Series A", 2, t0.AddDays(1)));
        h.MainItems.Add(Episode(SeriesAId, "Series A", 2, t0.AddDays(2)));
        h.MainItems.Add(Episode(SeriesBId, "Series B", 1, t0.AddDays(3)));
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A", Overview = "A" });
        h.SeriesItems.Add(new Series { Id = SeriesBId, Name = "Series B" });

        var digest = h.Service.GetNewSince(t0.AddDays(-7));

        Assert.Equal(2, digest.Movies.Count);
        Assert.Equal(2, digest.Series.Count);
        var a = digest.Series.Single(s => s.Id == SeriesAId);
        Assert.Equal(3, a.NewEpisodeCount);
        Assert.Equal([1, 2], a.Seasons);
        Assert.Equal("Series A", a.Title);
        // Series B has the newest episode, so it sorts first.
        Assert.Equal(SeriesBId, digest.Series[0].Id);
        Assert.Equal(6, digest.TotalItems); // 2 movies + 3 (Series A) + 1 (Series B) new episodes
        Assert.False(digest.IsEmpty);
    }

    [Fact]
    public void BuildsPosterAndDeepLinkUrls_WhenPublicUrlAndImagePresent()
    {
        var h = new Harness("https://media.example.org/");
        var withImage = Movie("Sicario", withImage: true);
        h.MainItems.Add(withImage);

        var digest = h.Service.GetNewSince(DateTime.UtcNow.AddDays(-7));

        var movie = Assert.Single(digest.Movies);
        Assert.Equal($"https://media.example.org/Items/{withImage.Id:N}/Images/Primary?maxWidth=300&quality=85", movie.PosterUrl);
        Assert.Equal($"https://media.example.org/web/#/details?id={withImage.Id:N}&serverId=srv-1", movie.DetailUrl);
    }

    [Fact]
    public void NoImage_MeansNoPosterUrl_ButStillADeepLink()
    {
        var h = new Harness();
        h.MainItems.Add(Movie("No Poster", withImage: false));

        var movie = Assert.Single(h.Service.GetNewSince(DateTime.UtcNow.AddDays(-7)).Movies);

        Assert.Null(movie.PosterUrl);
        Assert.NotNull(movie.DetailUrl);
    }

    [Fact]
    public void NoPublicUrl_MeansNoPosterAndNoDeepLink()
    {
        var h = new Harness(publicUrl: "");
        h.MainItems.Add(Movie("Offline", withImage: true));
        h.MainItems.Add(Episode(SeriesAId, "Series A", 1, DateTime.UtcNow));
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A" });

        var digest = h.Service.GetNewSince(DateTime.UtcNow.AddDays(-7));

        Assert.Null(digest.Movies[0].PosterUrl);
        Assert.Null(digest.Movies[0].DetailUrl);
        Assert.Null(digest.Series[0].PosterUrl);
        Assert.Null(digest.Series[0].DetailUrl);
    }

    [Fact]
    public void BoundsTheQuery_AndAsksForMoviesAndEpisodes()
    {
        var h = new Harness();

        h.Service.GetNewSince(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), limit: 750);

        var q = h.CapturedMainQuery!;
        Assert.Equal(750, q.Limit);
        Assert.True(q.Recursive);
        Assert.Contains(BaseItemKind.Movie, q.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Episode, q.IncludeItemTypes);
    }

    [Fact]
    public void WindowsItemsByDateCreated_InMemory()
    {
        var h = new Harness();
        var since = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        h.MainItems.Add(Movie("Old", created: since.AddDays(-1)));
        h.MainItems.Add(Movie("New", created: since.AddHours(1)));

        var digest = h.Service.GetNewSince(since);

        var movie = Assert.Single(digest.Movies);
        Assert.Equal("New", movie.Title);
    }

    [Fact]
    public void EpisodeWithoutASeries_IsIgnored()
    {
        var h = new Harness();
        h.MainItems.Add(Episode(Guid.Empty, string.Empty, 1, DateTime.UtcNow));

        var digest = h.Service.GetNewSince(DateTime.UtcNow.AddDays(-7));

        Assert.Empty(digest.Series);
        Assert.True(digest.IsEmpty);
    }

    [Fact]
    public void EmptyLibraryWindow_IsAnEmptyDigest()
    {
        var digest = new Harness().Service.GetNewSince(DateTime.UtcNow.AddDays(-7));

        Assert.True(digest.IsEmpty);
        Assert.Equal(0, digest.TotalItems);
    }

    [Fact]
    public void NeverCallsAMutatingLibraryMethod()
    {
        var h = new Harness();
        h.MainItems.Add(Movie("Read Only"));

        h.Service.GetNewSince(DateTime.UtcNow.AddDays(-7));

        h.Library.Verify(
            l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        h.Library.Verify(
            l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }
}
