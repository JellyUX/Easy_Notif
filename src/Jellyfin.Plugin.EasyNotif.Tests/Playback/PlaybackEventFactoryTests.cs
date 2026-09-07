using Jellyfin.Plugin.EasyNotif.Playback;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Playback;

/// <summary>
/// Covers <see cref="PlaybackEventFactory.TryBuild"/>: only movies and episodes past three minutes
/// are recorded, completion follows the played-to-completion flag / 20 minutes / 90 percent rule,
/// and episode metadata is carried over.
/// </summary>
public sealed class PlaybackEventFactoryTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Rejects_AStopUnderThreeMinutes()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };

        Assert.False(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(2).Ticks, false, User, Now, out _));
    }

    [Fact]
    public void Records_AStopAtThreeMinutes()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };

        Assert.True(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(3).Ticks, false, User, Now, out var evt));
        Assert.False(evt.Completed);
        Assert.Equal(Now, evt.Ts);
        Assert.Equal(User, evt.UserId);
    }

    [Fact]
    public void MarksCompleted_WhenPlayedToCompletion()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };

        Assert.True(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(5).Ticks, true, User, Now, out var evt));
        Assert.True(evt.Completed);
    }

    [Fact]
    public void MarksCompleted_PastTwentyMinutes_EvenWithoutRuntime()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };

        Assert.True(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(25).Ticks, false, User, Now, out var evt));
        Assert.True(evt.Completed);
    }

    [Fact]
    public void MarksCompleted_PastNinetyPercentOfRuntime()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film", RunTimeTicks = TimeSpan.FromMinutes(50).Ticks };

        Assert.True(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(46).Ticks, false, User, Now, out var evt));
        Assert.True(evt.Completed);
    }

    [Fact]
    public void DoesNotMarkCompleted_BelowTwentyMinutesAndUnderNinetyPercent()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film", RunTimeTicks = TimeSpan.FromMinutes(120).Ticks };

        Assert.True(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(15).Ticks, false, User, Now, out var evt));
        Assert.False(evt.Completed);
    }

    [Fact]
    public void Rejects_ANonVideoItem()
    {
        var item = new Audio { Id = Guid.NewGuid(), Name = "Track" };

        Assert.False(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(10).Ticks, false, User, Now, out _));
    }

    [Fact]
    public void Rejects_ANullItem()
        => Assert.False(PlaybackEventFactory.TryBuild(null, TimeSpan.FromMinutes(10).Ticks, false, User, Now, out _));

    [Fact]
    public void CarriesEpisodeMetadata()
    {
        var seriesId = Guid.NewGuid();
        var item = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Pilot",
            SeriesId = seriesId,
            SeriesName = "Bodyguard",
            RunTimeTicks = TimeSpan.FromMinutes(50).Ticks
        };

        Assert.True(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(10).Ticks, false, User, Now, out var evt));
        Assert.Equal("Episode", evt.Kind);
        Assert.Equal(seriesId, evt.SeriesId);
        Assert.Equal("Bodyguard", evt.SeriesName);
        Assert.Equal("Pilot", evt.Name);
    }

    [Fact]
    public void MovieHasNoSeries()
    {
        var item = new Movie { Id = Guid.NewGuid(), Name = "Film" };

        Assert.True(PlaybackEventFactory.TryBuild(item, TimeSpan.FromMinutes(10).Ticks, false, User, Now, out var evt));
        Assert.Equal("Movie", evt.Kind);
        Assert.Null(evt.SeriesId);
        Assert.Null(evt.SeriesName);
    }
}
