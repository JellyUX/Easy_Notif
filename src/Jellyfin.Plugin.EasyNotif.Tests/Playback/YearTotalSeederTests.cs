using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Playback;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Playback;

/// <summary>
/// Covers <see cref="YearTotalSeeder"/> (FEAT-08, DEC-81): only items marked played whose last play
/// falls in the current year before tracking started are counted, the seed runs once, and tracking
/// that started before this year seeds nothing but is still marked done.
/// </summary>
public sealed class YearTotalSeederTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Installed = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);

    private readonly User _alice = new("alice", "Default", "Default") { Id = Guid.NewGuid() };
    private readonly User _bob = new("bob", "Default", "Default") { Id = Guid.NewGuid() };
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly FakePlaybackHistoryStore _history = new() { SeedState = new(Installed, null) };
    private readonly FakeEasyNotifLog _log = new();
    private readonly Dictionary<Guid, List<BaseItem>> _played = [];

    public YearTotalSeederTests()
    {
        _users.Setup(u => u.GetUsers()).Returns([_alice, _bob]);
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => _played.TryGetValue(q.User!.Id, out var items) ? items : []);
    }

    private YearTotalSeeder Seeder() => new(_history, _users.Object, _library.Object, _userData.Object, _log);

    private void Played(User user, DateTime? lastPlayed, bool played = true)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        if (!_played.TryGetValue(user.Id, out var items))
        {
            _played[user.Id] = items = [];
        }

        items.Add(item);
        _userData.Setup(d => d.GetUserData(user, item)).Returns(new UserItemData { Key = item.Id.ToString("N"), Played = played, LastPlayedDate = lastPlayed });
    }

    [Fact]
    public void CountsThisYearsPlaysBeforeTracking_Only()
    {
        Played(_alice, new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc));
        Played(_alice, new DateTime(2026, 9, 7, 23, 0, 0, DateTimeKind.Utc));
        Played(_alice, new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc)); // last year
        Played(_alice, new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc)); // tracked already
        Played(_alice, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), played: false);
        Played(_alice, null);

        Seeder().RunOnce(Now);

        var (year, dates) = Assert.Single(_history.AppliedSeeds);
        Assert.Equal(2026, year);
        Assert.Equal(2, dates[_alice.Id].Count);
        Assert.False(dates.ContainsKey(_bob.Id));
        Assert.Contains(_log.Entries, e => e.EventType == "history.seeded" && (int)e.Fields!["views"]! == 2);
    }

    [Fact]
    public void RunsOnce()
    {
        Played(_bob, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        Seeder().RunOnce(Now);
        Seeder().RunOnce(Now.AddMinutes(15));

        Assert.Single(_history.AppliedSeeds);
        _library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Exactly(2)); // one per user, first run only
    }

    [Fact]
    public void TrackingFromAnEarlierYear_SeedsNothing_ButMarksItDone()
    {
        _history.SeedState = new(new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc), null);

        Seeder().RunOnce(Now);

        Assert.Empty(Assert.Single(_history.AppliedSeeds).Dates);
        Assert.NotNull(_history.SeedState.SeededUtc);
        _library.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void TrackingNotStartedYet_WaitsForTheNextTick()
    {
        _history.SeedState = new(default, null);

        Seeder().RunOnce(Now);

        Assert.Empty(_history.AppliedSeeds);
    }
}
