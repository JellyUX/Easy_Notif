using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Recap;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Recap;

/// <summary>
/// Covers <see cref="ResumeLookup"/> (FEAT-03, DEC-77, DEC-78): started, unfinished items played in the
/// last 30 days and visible to the user, at most three, most recent first; the series poster for an
/// episode, linked with a public URL and attached inline without one.
/// </summary>
public sealed class ResumeLookupTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly long Hour = TimeSpan.TicksPerHour;

    private readonly User _user = new("alex", "Default", "Default") { Id = UserId };
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IAccessFilter> _access = new();
    private readonly Mock<IPosterCache> _posters = new();
    private readonly FakeEasyNotifLog _log = new();
    private readonly List<BaseItem> _resumable = [];

    public ResumeLookupTests()
    {
        _users.Setup(u => u.GetUserById(UserId)).Returns(_user);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => _resumable);
        _access.Setup(a => a.VisibleTo(UserId, It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns((Guid _, IReadOnlyCollection<Guid> ids) => ids.ToHashSet());
        _posters.Setup(p => p.GetInlineAsync(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BaseItem item, CancellationToken _) => new EmailAttachment { FileName = "p.jpg", Content = [1], ContentId = "p-" + item.Id.ToString("N") });
    }

    private ResumeLookup Lookup(string publicUrl = "https://media.example.org")
        => new(_users.Object, _library.Object, _userData.Object, _access.Object, _posters.Object,
            new FakeConfigAccessor(new PluginConfiguration { PublicServerUrl = publicUrl }), new ServerLinkContext("srv-1", "Home"), _log);

    private T Started<T>(string name, double hoursIn, DateTime lastPlayed, bool played = false, long? runtime = 2 * TimeSpan.TicksPerHour)
        where T : BaseItem, new()
    {
        var item = new T { Id = Guid.NewGuid(), Name = name, RunTimeTicks = runtime };
        _resumable.Add(item);
        _userData.Setup(d => d.GetUserData(_user, item)).Returns(new UserItemData
        {
            Key = name,
            PlaybackPositionTicks = (long)(hoursIn * Hour),
            LastPlayedDate = lastPlayed,
            Played = played
        });
        return item;
    }

    [Fact]
    public void KeepsTheThreeMostRecentlyPlayed_NewestFirst()
    {
        Started<Movie>("Oldest", 1, Now.AddDays(-4));
        Started<Movie>("Newest", 1, Now.AddHours(-1));
        Started<Movie>("Middle", 1, Now.AddDays(-2));
        Started<Movie>("Second", 1, Now.AddDays(-1));

        var section = Lookup().For(UserId, Now);

        Assert.Equal(["Newest", "Second", "Middle"], section.Entries.Select(e => e.Title));
    }

    [Fact]
    public void DropsOldPlayedUnstartedAndForbiddenItems()
    {
        Started<Movie>("Abandoned", 1, Now.AddDays(-31));
        Started<Movie>("Finished", 1, Now.AddDays(-1), played: true);
        Started<Movie>("NotStarted", 0, Now.AddDays(-1));
        var forbidden = Started<Movie>("Forbidden", 1, Now.AddDays(-1));
        Started<Movie>("Kept", 1, Now.AddDays(-29));
        _access.Setup(a => a.VisibleTo(UserId, It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns((Guid _, IReadOnlyCollection<Guid> ids) => ids.Where(id => id != forbidden.Id).ToHashSet());

        var section = Lookup().For(UserId, Now);

        Assert.Equal(["Kept"], section.Entries.Select(e => e.Title));
    }

    [Fact]
    public void Episode_ShowsItsSeries_Label_AndRemainingTime()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Slow Horses", ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "/s.jpg" }] };
        _library.Setup(l => l.GetItemById(series.Id)).Returns(series);
        var episode = Started<Episode>("Mr. Pilkington's Fish", 0.5, Now.AddDays(-1), runtime: 56 * TimeSpan.TicksPerMinute);
        episode.SeriesId = series.Id;
        episode.SeriesName = "Slow Horses";
        episode.ParentIndexNumber = 2;
        episode.IndexNumber = 3;

        var entry = Assert.Single(Lookup().For(UserId, Now).Entries);

        Assert.Equal("Slow Horses", entry.SeriesTitle);
        Assert.Equal("S02E03", entry.EpisodeLabel);
        Assert.Equal(26, entry.RemainingMinutes);
        Assert.Equal($"https://media.example.org/Items/{series.Id:N}/Images/Primary?maxWidth=300&quality=85", entry.PosterUrl);
        Assert.Equal($"https://media.example.org/web/#/details?id={episode.Id:N}&serverId=srv-1", entry.DetailUrl);
    }

    [Fact]
    public void NoPublicUrl_AttachesThePosterInline_AndDoesNotLink()
    {
        var movie = Started<Movie>("Arrival", 1, Now.AddDays(-1));

        var section = Lookup(publicUrl: string.Empty).For(UserId, Now);

        var entry = Assert.Single(section.Entries);
        Assert.Equal($"cid:p-{movie.Id:N}", entry.PosterUrl);
        Assert.Null(entry.DetailUrl);
        Assert.Equal($"p-{movie.Id:N}", Assert.Single(section.Posters).ContentId);
    }

    [Fact]
    public void UnknownUser_OrJellyfinError_GivesAnEmptySection()
    {
        Assert.Empty(Lookup().For(Guid.NewGuid(), Now).Entries);

        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new InvalidOperationException("boom"));
        Assert.Empty(Lookup().For(UserId, Now).Entries);
        Assert.Contains(_log.Entries, e => e.Level == "Warn" && e.EventType == "recap.resume-error");
    }

    [Theory]
    [InlineData(2L * 36_000_000_000, 36_000_000_000, 60)]
    [InlineData(36_000_000_000, 35_400_000_001, 1)]
    [InlineData(36_000_000_000, 40_000_000_000, 0)]
    public void RemainingMinutes_RoundsUp_AndNeverGoesNegative(long runtime, long position, int expected)
        => Assert.Equal(expected, ResumeLookup.RemainingMinutes(runtime, position));

    [Fact]
    public void RemainingMinutes_UnknownRuntime_IsNull()
        => Assert.Null(ResumeLookup.RemainingMinutes(null, 10));
}
