using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Services;

/// <summary>
/// Covers <see cref="FollowedSeriesService"/>: a series the caller cannot see answers exactly like a
/// series that does not exist (SEC-11, no enumeration), and only visible series can be followed.
/// </summary>
public sealed class FollowedSeriesServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly List<SeriesSubscription> _subscriptions = [];
    private readonly Mock<ISeriesNotifyStore> _store = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IAccessFilter> _access = new();
    private readonly HashSet<Guid> _hidden = [];

    public FollowedSeriesServiceTests()
    {
        _store.Setup(s => s.Subscriptions()).Returns(() => [.. _subscriptions]);
        _store
            .Setup(s => s.Subscribe(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Callback<Guid, Guid, string, DateTime>((u, s, src, at) => _subscriptions.Add(new SeriesSubscription(u, s, at, src)));
        _store
            .Setup(s => s.Unsubscribe(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .Callback<Guid, Guid>((u, s) => _subscriptions.RemoveAll(x => x.UserId == u && x.SeriesId == s));
        _access
            .Setup(a => a.VisibleTo(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns<Guid, IReadOnlyCollection<Guid>>((_, ids) => ids.Where(id => !_hidden.Contains(id)).ToHashSet());
    }

    private FollowedSeriesService Service() => new(_store.Object, _access.Object, _library.Object, new FixedTime(Now));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private T Add<T>(string name, bool visible = true)
        where T : BaseItem, new()
    {
        var item = new T { Id = Guid.NewGuid(), Name = name };
        _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        if (!visible)
        {
            _hidden.Add(item.Id);
        }

        return item;
    }

    [Fact]
    public void ForbiddenAndMissingSeries_AreIndistinguishable()
    {
        var forbidden = Add<Series>("Hidden", visible: false);
        var missing = Guid.NewGuid();
        var service = Service();

        Assert.Equal(service.IsFollowing(UserId, missing), service.IsFollowing(UserId, forbidden.Id));
        Assert.Null(service.IsFollowing(UserId, forbidden.Id));
        Assert.Equal(service.Follow(UserId, missing, "bell"), service.Follow(UserId, forbidden.Id, "bell"));
        Assert.False(service.Follow(UserId, forbidden.Id, "bell"));
        Assert.Equal(service.Unfollow(UserId, missing), service.Unfollow(UserId, forbidden.Id));
        Assert.False(service.Unfollow(UserId, forbidden.Id));
        Assert.Empty(_subscriptions);
    }

    [Fact]
    public void ANonSeriesItem_IsTreatedAsMissing()
    {
        var movie = Add<Movie>("A film");

        Assert.Null(Service().IsFollowing(UserId, movie.Id));
        Assert.False(Service().Follow(UserId, movie.Id, "bell"));
        Assert.Null(Service().IsFollowing(UserId, Guid.Empty));
    }

    [Fact]
    public void VisibleSeries_CanBeFollowedAndUnfollowed_Idempotently()
    {
        var series = Add<Series>("Slow Horses");
        var service = Service();

        Assert.False(service.IsFollowing(UserId, series.Id));
        Assert.True(service.Follow(UserId, series.Id, "panel"));
        Assert.True(service.IsFollowing(UserId, series.Id));
        var sub = Assert.Single(_subscriptions);
        Assert.Equal("panel", sub.Source);
        Assert.Equal(Now.UtcDateTime, sub.Since);

        Assert.True(service.Unfollow(UserId, series.Id));
        Assert.True(service.Unfollow(UserId, series.Id));
        Assert.False(service.IsFollowing(UserId, series.Id));
    }

    [Fact]
    public void AFollowedSeriesTheUserLostAccessTo_CanStillBeDropped_ButIsNotListed()
    {
        var series = Add<Series>("Severance");
        var service = Service();
        service.Follow(UserId, series.Id, "bell");
        _hidden.Add(series.Id);

        Assert.Empty(service.List(UserId));
        Assert.Null(service.IsFollowing(UserId, series.Id));
        Assert.True(service.Unfollow(UserId, series.Id));
        Assert.Empty(_subscriptions);
    }

    [Fact]
    public void List_ReturnsOnlyTheCallersVisibleSeries_ByName()
    {
        var b = Add<Series>("Bluey");
        var a = Add<Series>("Andor");
        var gone = Guid.NewGuid();
        _subscriptions.Add(new SeriesSubscription(UserId, b.Id, Now.UtcDateTime, "bell"));
        _subscriptions.Add(new SeriesSubscription(UserId, a.Id, Now.UtcDateTime, "bell"));
        _subscriptions.Add(new SeriesSubscription(UserId, gone, Now.UtcDateTime, "bell"));
        _subscriptions.Add(new SeriesSubscription(Guid.NewGuid(), Add<Series>("Other").Id, Now.UtcDateTime, "bell"));

        var list = Service().List(UserId);

        Assert.Equal(["Andor", "Bluey"], list.Select(s => s.Name));
        Assert.Equal(a.Id, list[0].Id);
    }
}
