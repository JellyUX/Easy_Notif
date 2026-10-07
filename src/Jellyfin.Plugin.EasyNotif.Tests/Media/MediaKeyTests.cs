using Jellyfin.Plugin.EasyNotif.Media;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="MediaKey"/> against the shapes measured on the integration bench (phase 05):
/// numbered episode, multi-episode file, dated episode, unnumbered episode, and movies.
/// </summary>
public sealed class MediaKeyTests
{
    private static readonly Guid SeriesId = Guid.Parse("d2517219-0000-0000-0000-000000000001");

    [Fact]
    public void NumberedEpisode_KeyIgnoresTheItemId()
    {
        var first = new Episode { Id = Guid.NewGuid(), SeriesId = SeriesId, ParentIndexNumber = 1, IndexNumber = 1 };
        var replaced = new Episode { Id = Guid.NewGuid(), SeriesId = SeriesId, ParentIndexNumber = 1, IndexNumber = 1 };

        Assert.Equal($"ep:{SeriesId:N}:1:1", MediaKey.For(first));
        Assert.Equal(MediaKey.For(first), MediaKey.For(replaced));
    }

    [Fact]
    public void MultiEpisodeFile_KeyCarriesTheEnd()
        => Assert.Equal(
            $"ep:{SeriesId:N}:1:2-3",
            MediaKey.For(new Episode { Id = Guid.NewGuid(), SeriesId = SeriesId, ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 }));

    [Fact]
    public void DatedEpisode_FallsBackToThePremiereDate()
        => Assert.Equal(
            $"ep:{SeriesId:N}:2022-02-18",
            MediaKey.For(new Episode { Id = Guid.NewGuid(), SeriesId = SeriesId, ParentIndexNumber = 1, PremiereDate = new DateTime(2022, 2, 18, 0, 0, 0, DateTimeKind.Utc) }));

    [Fact]
    public void UnnumberedOrUnattachedEpisode_FallsBackToTheId()
    {
        var unnumbered = new Episode { Id = Guid.NewGuid(), SeriesId = SeriesId };
        var noSeries = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IndexNumber = 1 };

        Assert.Equal($"id:{unnumbered.Id:N}", MediaKey.For(unnumbered));
        Assert.Equal($"id:{noSeries.Id:N}", MediaKey.For(noSeries));
    }

    [Fact]
    public void Movie_PrefersTmdb_ThenImdb_ThenTheId()
    {
        var both = new Movie { Id = Guid.NewGuid() };
        both.SetProviderId(MetadataProvider.Tmdb, "603");
        both.SetProviderId(MetadataProvider.Imdb, "tt0133093");
        var imdbOnly = new Movie { Id = Guid.NewGuid() };
        imdbOnly.SetProviderId(MetadataProvider.Imdb, "tt0133093");
        var none = new Movie { Id = Guid.NewGuid() };

        Assert.Equal("movie:tmdb:603", MediaKey.For(both));
        Assert.Equal("movie:imdb:tt0133093", MediaKey.For(imdbOnly));
        Assert.Equal($"id:{none.Id:N}", MediaKey.For(none));
    }
}
