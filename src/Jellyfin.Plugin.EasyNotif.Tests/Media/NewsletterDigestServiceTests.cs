using Jellyfin.Data.Enums;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
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

        public FakeAddedItemsStore AddedItems { get; } = new();

        /// <summary>Items returned by the by-DateCreated fallback query (the recently-added scan).</summary>
        public List<BaseItem> MainItems { get; } = [];

        /// <summary>Items resolvable by id (tracked items and series parents).</summary>
        public List<BaseItem> ById { get; } = [];

        public Mock<IPosterCache> PosterCache { get; } = new();

        public FakeAnnouncedStore Announced { get; } = new();

        /// <summary>The library lookup; every item has no library unless a test maps it.</summary>
        public Mock<ILibraryCatalog> Libraries { get; } = new();

        /// <summary>Every real item of the library, answering the "same key under another id" queries.</summary>
        public List<BaseItem> Existing { get; } = [];

        public NewsletterDigestService Service { get; }

        public InternalItemsQuery? CapturedMainQuery { get; private set; }

        public Harness(string publicUrl = "https://media.example.org")
        {
            Config = new FakeConfigAccessor(new PluginConfiguration { PublicServerUrl = publicUrl });
            Library
                .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns<InternalItemsQuery>(q =>
                {
                    if (q.ItemIds is { Length: > 0 } ids)
                    {
                        return ById.Where(i => ids.Contains(i.Id)).ToList();
                    }

                    if (q.AncestorIds is { Length: > 0 } ancestors)
                    {
                        return Existing.OfType<Episode>().Where(e => ancestors.Contains(e.SeriesId)).ToList<BaseItem>();
                    }

                    if (q.HasAnyProviderId is { Count: > 0 } providers)
                    {
                        return Existing.OfType<Movie>()
                            .Where(m => providers.Any(p => m.GetProviderId(p.Key) == p.Value))
                            .ToList<BaseItem>();
                    }

                    CapturedMainQuery = q;
                    return MainItems;
                });
            PosterCache
                .Setup(p => p.GetInlineAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((EmailAttachment?)null);
            Libraries.Setup(l => l.LibrariesOf(It.IsAny<BaseItem>())).Returns([]);
            Service = new NewsletterDigestService(
                Library.Object, Config, new ServerLinkContext("srv-1", "Home"), AddedItems, PosterCache.Object, Announced, Libraries.Object, new FakeEasyNotifLog());
        }

        public List<BaseItem> SeriesItems => ById;

        public NewsletterDigest Digest(DateTime sinceUtc, int limit = 1000)
            => Service.GetNewSinceAsync(sinceUtc, CancellationToken.None, limit).GetAwaiter().GetResult();
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

        var digest = h.Digest(t0.AddDays(-7));

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
        // Each episode is kept so the digest can be narrowed per recipient; access covers all six items.
        Assert.Equal([1, 2, 2], a.EpisodeList.Select(e => e.Season!.Value).Order());
        Assert.Equal(6, digest.ItemIds.Count);
        Assert.Equal(h.MainItems.Select(i => i.Id).Order(), digest.ItemIds.Order());
    }

    [Fact]
    public void BuildsPosterAndDeepLinkUrls_WhenPublicUrlAndImagePresent()
    {
        var h = new Harness("https://media.example.org/");
        var withImage = Movie("Sicario", withImage: true);
        h.MainItems.Add(withImage);

        var digest = h.Digest(DateTime.UtcNow.AddDays(-7));

        var movie = Assert.Single(digest.Movies);
        Assert.Equal($"https://media.example.org/Items/{withImage.Id:N}/Images/Primary?maxWidth=300&quality=85", movie.PosterUrl);
        Assert.Equal($"https://media.example.org/web/#/details?id={withImage.Id:N}&serverId=srv-1", movie.DetailUrl);
    }

    [Fact]
    public void NoImage_MeansNoPosterUrl_ButStillADeepLink()
    {
        var h = new Harness();
        h.MainItems.Add(Movie("No Poster", withImage: false));

        var movie = Assert.Single(h.Digest(DateTime.UtcNow.AddDays(-7)).Movies);

        Assert.Null(movie.PosterUrl);
        Assert.NotNull(movie.DetailUrl);
    }

    [Fact]
    public void NoPublicUrl_MeansNoDeepLink_AndNoPosterWhenTheCacheReturnsNothing()
    {
        var h = new Harness(publicUrl: "");
        h.MainItems.Add(Movie("Offline", withImage: true));
        h.MainItems.Add(Episode(SeriesAId, "Series A", 1, DateTime.UtcNow));
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A" });

        var digest = h.Digest(DateTime.UtcNow.AddDays(-7));

        Assert.Null(digest.Movies[0].PosterUrl);
        Assert.Null(digest.Movies[0].DetailUrl);
        Assert.Null(digest.Series[0].PosterUrl);
        Assert.Null(digest.Series[0].DetailUrl);
        Assert.Empty(digest.Posters);
    }

    [Fact]
    public void NoPublicUrl_AttachesInlinePosters_WhenTheCacheResolvesThem()
    {
        var h = new Harness(publicUrl: "");
        var movie = Movie("Offline", withImage: true);
        h.MainItems.Add(movie);
        h.PosterCache
            .Setup(p => p.GetInlineAsync(It.Is<BaseItem>(i => i.Id == movie.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmailAttachment
            {
                FileName = "poster.jpg",
                Content = [1, 2, 3],
                ContentType = "image/jpeg",
                ContentId = "poster-" + movie.Id.ToString("N")
            });

        var digest = h.Digest(DateTime.UtcNow.AddDays(-7));

        Assert.Equal($"cid:poster-{movie.Id:N}", digest.Movies[0].PosterUrl);
        Assert.Single(digest.Posters);
    }

    [Fact]
    public void PublicUrl_NeverCallsThePosterCache()
    {
        var h = new Harness();
        h.MainItems.Add(Movie("Linked", withImage: true));

        h.Digest(DateTime.UtcNow.AddDays(-7));

        h.PosterCache.Verify(p => p.GetInlineAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void BoundsTheQuery_AndAsksForMoviesAndEpisodes()
    {
        var h = new Harness();

        h.Digest(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc), limit: 750);

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

        var digest = h.Digest(since);

        var movie = Assert.Single(digest.Movies);
        Assert.Equal("New", movie.Title);
    }

    [Fact]
    public void IncludesAnItemTrackedAsAdded_EvenWhenItsDateCreatedIsOld()
    {
        var h = new Harness();
        var since = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var oldMovie = Movie("Copied From Archive", created: new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        h.ById.Add(oldMovie);              // resolvable by id, but not in the recently-added scan
        h.AddedItems.Seed(oldMovie.Id, since.AddHours(2)); // the ItemAdded event recorded it as new

        var digest = h.Digest(since);

        var movie = Assert.Single(digest.Movies);
        Assert.Equal("Copied From Archive", movie.Title);
    }

    [Fact]
    public void DoesNotDoubleCountAnItemThatIsBothTrackedAndRecentByDate()
    {
        var h = new Harness();
        var since = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var movie = Movie("Both", created: since.AddHours(1));
        h.MainItems.Add(movie);
        h.ById.Add(movie);
        h.AddedItems.Seed(movie.Id, since.AddHours(1));

        Assert.Single(h.Digest(since).Movies);
    }

    [Fact]
    public void EpisodeWithoutASeries_IsIgnored()
    {
        var h = new Harness();
        h.MainItems.Add(Episode(Guid.Empty, string.Empty, 1, DateTime.UtcNow));

        var digest = h.Digest(DateTime.UtcNow.AddDays(-7));

        Assert.Empty(digest.Series);
        Assert.True(digest.IsEmpty);
    }

    [Fact]
    public void EmptyLibraryWindow_IsAnEmptyDigest()
    {
        var digest = new Harness().Digest(DateTime.UtcNow.AddDays(-7));

        Assert.True(digest.IsEmpty);
        Assert.Equal(0, digest.TotalItems);
    }

    [Fact]
    public void NeverCallsAMutatingLibraryMethod()
    {
        var h = new Harness();
        h.MainItems.Add(Movie("Read Only"));

        h.Digest(DateTime.UtcNow.AddDays(-7));

        h.Library.Verify(
            l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
            Times.Never);
        h.Library.Verify(
            l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    private static readonly DateTime W0 = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime W1 = W0.AddDays(7);

    private static Episode Numbered(int season, int number)
        => new() { Id = Guid.NewGuid(), SeriesId = SeriesAId, SeriesName = "Series A", ParentIndexNumber = season, IndexNumber = number, DateCreated = W0 };

    private static Movie WithTmdb(string name, string tmdb)
    {
        var m = Movie(name, created: W0);
        m.SetProviderId(MetadataProvider.Tmdb, tmdb);
        return m;
    }

    // Records an item as added at `at` and resolvable by id, as the ItemAdded tracking would.
    private static void Track(Harness h, BaseItem item, DateTime at)
    {
        h.ById.Add(item);
        h.AddedItems.Seed(item.Id, at);
    }

    // Sends the window starting at `since` as the dispatcher would: build, then record at `runUtc`.
    private static NewsletterDigest Send(Harness h, DateTime since, DateTime runUtc)
    {
        var digest = h.Digest(since);
        h.Service.MarkAnnounced(digest.Keys, runUtc);
        return digest;
    }

    [Fact]
    public void SameIdReplayed_IsNotAnnouncedInTheNextWindow()
    {
        var h = new Harness();
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A" });
        var episode = Numbered(1, 1);
        Track(h, episode, W0.AddHours(1));
        Assert.Equal(1, Send(h, W0, W1).TotalItems);

        h.AddedItems.Seed(episode.Id, W1.AddHours(1)); // a repeated ItemAdded for the same id

        Assert.True(h.Digest(W1).IsEmpty);
    }

    [Fact]
    public void SameEpisodeUnderAnotherId_IsNotAnnouncedInTheNextWindow()
    {
        var h = new Harness();
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A" });
        var original = Numbered(1, 1);
        Track(h, original, W0.AddHours(1));
        Send(h, W0, W1);

        h.ById.Remove(original); // replaced, renamed or moved: the old id is gone
        Track(h, Numbered(1, 1), W1.AddHours(1));
        var fresh = Numbered(1, 2);
        Track(h, fresh, W1.AddHours(1));

        var digest = h.Digest(W1);

        Assert.Equal([fresh.Id], digest.ItemIds);
    }

    [Fact]
    public void SameMovieWithTheSameProviderIds_IsNotAnnouncedInTheNextWindow()
    {
        var h = new Harness();
        var original = WithTmdb("Arrival", "329865");
        Track(h, original, W0.AddHours(1));
        Send(h, W0, W1);

        h.ById.Remove(original);
        Track(h, WithTmdb("Arrival (2016)", "329865"), W1.AddHours(1));

        Assert.True(h.Digest(W1).IsEmpty);
    }

    [Fact]
    public void RetryOfTheSentWindow_StillFindsItsContent()
    {
        var h = new Harness();
        var movie = WithTmdb("Dune", "438631");
        Track(h, movie, W0.AddHours(1));
        Send(h, W0, W1);

        var rebuilt = h.Digest(W0); // the retry queue rebuilds the original window

        Assert.Equal([movie.Id], rebuilt.ItemIds);
    }

    [Fact]
    public void TwoIdsWithOneKeyInAWindow_AreListedOnce()
    {
        var h = new Harness();
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A" });
        Track(h, Numbered(2, 10), W0.AddHours(1));
        Track(h, Numbered(2, 10), W0.AddHours(1)); // a move: the new id arrives before the old one leaves

        var digest = h.Digest(W0);

        Assert.Equal(1, digest.TotalItems);
        Assert.Single(digest.Keys);
    }

    [Fact]
    public void KeyAlreadyInTheLibraryUnderAnotherId_IsSkipped_ButAVirtualEpisodeDoesNotCount()
    {
        var h = new Harness();
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A" });
        var older = Numbered(1, 1); // present before the registry existed, never announced
        h.Existing.Add(older);
        var missing = Numbered(1, 2);
        missing.IsVirtualItem = true; // a "missing episode" placeholder
        h.Existing.Add(missing);

        var copy = Numbered(1, 1);
        var real = Numbered(1, 2);
        Track(h, copy, W0.AddHours(1));
        Track(h, real, W0.AddHours(1));
        h.Existing.AddRange([copy, real]);

        var digest = h.Digest(W0);

        Assert.Equal([real.Id], digest.ItemIds);
    }

    [Fact]
    public void MovieAlreadyInTheLibraryWithTheSameProviderId_IsSkipped()
    {
        var h = new Harness();
        h.Existing.Add(WithTmdb("Matrix", "603"));
        var copy = WithTmdb("Matrix 4K", "603");
        Track(h, copy, W0.AddHours(1));
        h.Existing.Add(copy);

        Assert.True(h.Digest(W0).IsEmpty);
    }

    [Fact]
    public void RecordsTheLibrariesOfEveryMovieAndEpisode()
    {
        var h = new Harness();
        var films = Guid.NewGuid();
        var shows = Guid.NewGuid();
        var movie = Movie("Arrival");
        var episode = Episode(SeriesAId, "Series A", 1, DateTime.UtcNow);
        h.MainItems.Add(movie);
        h.MainItems.Add(episode);
        h.SeriesItems.Add(new Series { Id = SeriesAId, Name = "Series A" });
        h.Libraries.Setup(l => l.LibrariesOf(movie)).Returns([films]);
        h.Libraries.Setup(l => l.LibrariesOf(episode)).Returns([shows]);

        var digest = h.Digest(DateTime.UtcNow.AddDays(-1));

        Assert.Equal([films], digest.ItemLibraries[movie.Id]);
        Assert.Equal([shows], digest.ItemLibraries[episode.Id]);
        Assert.Equal(2, digest.ItemLibraries.Count);
    }
}
