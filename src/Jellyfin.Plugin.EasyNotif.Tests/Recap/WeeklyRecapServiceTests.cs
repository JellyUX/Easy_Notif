using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Playback;
using Jellyfin.Plugin.EasyNotif.Recap;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Recap;

/// <summary>
/// Covers <see cref="WeeklyRecapService.BuildFor"/>: movies list one line each, a single episode of
/// a series lists as an episode, two or more episodes of one series collapse to a group line, the
/// year-to-date totals come from the rollup, and <c>PartialSince</c> is set only while the annual
/// total does not yet cover a full year.
/// </summary>
public sealed class WeeklyRecapServiceTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static PlaybackEvent Movie(string name, DateTime ts, bool completed = true)
        => new(ts, User, Guid.NewGuid(), "Movie", null, name, null, 0, 0, completed);

    private static PlaybackEvent Episode(string name, Guid seriesId, string seriesName, DateTime ts, bool completed = true)
        => new(ts, User, Guid.NewGuid(), "Episode", seriesId, name, seriesName, 0, 0, completed);

    private static WeeklyRecapService Build(FakePlaybackHistoryStore history, User? user = null)
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(User)).Returns(user);
        return new WeeklyRecapService(history, users.Object);
    }

    [Fact]
    public void GroupsSeriesWithTwoOrMoreEpisodes_KeepsASingleEpisodeAsAnEpisode()
    {
        var history = new FakePlaybackHistoryStore();
        var seriesA = Guid.NewGuid();
        var seriesB = Guid.NewGuid();
        history.Seed(Movie("Dune", Now.AddDays(-1)));
        history.Seed(Episode("A pilot", seriesA, "Series A", Now.AddDays(-2)));
        history.Seed(Episode("B e1", seriesB, "Series B", Now.AddDays(-3)));
        history.Seed(Episode("B e2", seriesB, "Series B", Now.AddDays(-2)));
        history.Seed(Episode("B e3", seriesB, "Series B", Now.AddHours(-5)));

        var model = Build(history).BuildFor(User, Now);

        Assert.Equal(3, model.Watched.Count);
        Assert.Equal(RecapKind.Movie, model.Watched.Single(w => w.Title == "Dune").Kind);
        var a = model.Watched.Single(w => w.Title == "A pilot");
        Assert.Equal(RecapKind.Episode, a.Kind);
        Assert.Equal("Series A", a.SeriesTitle);
        var b = model.Watched.Single(w => w.Title == "Series B");
        Assert.Equal(RecapKind.Series, b.Kind);
        Assert.Equal(3, b.EpisodeCount);
        // Newest first.
        Assert.Equal("Series B", model.Watched[0].Title);
    }

    [Fact]
    public void QuietWeek_WhenNothingWatched()
    {
        var model = Build(new FakePlaybackHistoryStore()).BuildFor(User, Now);

        Assert.True(model.QuietWeek);
        Assert.Empty(model.Watched);
    }

    [Fact]
    public void PartialSince_SetWhenTrackingStartedAfterTheFirstOfTheRecapYear()
    {
        var history = new FakePlaybackHistoryStore
        {
            YearToDate = new YearToDate(10, 7, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc))
        };

        var model = Build(history).BuildFor(User, Now);

        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), model.PartialSince);
        Assert.Equal(10, model.YearTotal);
        Assert.Equal(7, model.YearCompleted);
    }

    [Fact]
    public void PartialSince_NullWhenTrackingStartedBeforeTheRecapYear()
    {
        var history = new FakePlaybackHistoryStore
        {
            YearToDate = new YearToDate(3, 3, new DateTime(2025, 12, 20, 0, 0, 0, DateTimeKind.Utc))
        };

        Assert.Null(Build(history).BuildFor(User, Now).PartialSince);
    }

    [Fact]
    public void UserName_FromTheUserManager_OrNull()
    {
        var history = new FakePlaybackHistoryStore();

        Assert.Null(Build(history).BuildFor(User, Now).UserName);
        Assert.Equal(
            "bob",
            Build(history, new User("bob", "Default", "Default") { Id = User }).BuildFor(User, Now).UserName);
    }
}
