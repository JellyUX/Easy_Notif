using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="AccessFilter"/>: it keeps only the items <c>IsVisibleStandalone</c> accepts for
/// the user (a forbidden library and an exceeded rating both answer false there, as measured on the
/// integration bench), and fails closed for an unknown user, a vanished item or a Jellyfin error.
/// </summary>
public sealed class AccessFilterTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly User _user = new("kid", "Default", "Default") { Id = UserId };
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly FakeEasyNotifLog _log = new();

    public AccessFilterTests()
        => _users.Setup(u => u.GetUserById(UserId)).Returns(_user);

    private AccessFilter Filter() => new(_users.Object, _library.Object, _log);

    private T Item<T>(bool visible)
        where T : BaseItem, new()
    {
        var mock = new Mock<T>();
        mock.Setup(i => i.IsVisibleStandalone(_user)).Returns(visible);
        var item = mock.Object;
        item.Id = Guid.NewGuid();
        _library.Setup(l => l.GetItemById(item.Id)).Returns(item);
        return item;
    }

    [Fact]
    public void KeepsVisibleItems_DropsForbiddenLibraryAndExceededRating()
    {
        var allowed = Item<Movie>(visible: true);
        var forbiddenLibrary = Item<Movie>(visible: false);
        var exceededRating = Item<Episode>(visible: false);

        var visible = Filter().VisibleTo(UserId, [allowed.Id, forbiddenLibrary.Id, exceededRating.Id]);

        Assert.Equal([allowed.Id], visible);
    }

    [Fact]
    public void UnknownUser_SeesNothing_AndIsLogged()
    {
        var allowed = Item<Movie>(visible: true);

        var visible = Filter().VisibleTo(Guid.NewGuid(), [allowed.Id]);

        Assert.Empty(visible);
        Assert.Contains(_log.Entries, e => e.Level == "Warn" && e.EventType == "access.unknown-user");
    }

    [Fact]
    public void VanishedItem_IsHidden()
    {
        var visible = Filter().VisibleTo(UserId, [Guid.NewGuid()]);

        Assert.Empty(visible);
    }

    [Fact]
    public void JellyfinError_HidesTheItem_KeepsTheOthers_AndIsLogged()
    {
        var allowed = Item<Movie>(visible: true);
        var broken = Guid.NewGuid();
        _library.Setup(l => l.GetItemById(broken)).Throws(new InvalidOperationException("boom"));

        var visible = Filter().VisibleTo(UserId, [broken, allowed.Id]);

        Assert.Equal([allowed.Id], visible);
        Assert.Contains(_log.Entries, e => e.Level == "Warn" && e.EventType == "access.error" && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void NoCandidates_DoesNotResolveTheUser()
    {
        Assert.Empty(Filter().VisibleTo(UserId, []));
        _users.Verify(u => u.GetUserById(It.IsAny<Guid>()), Times.Never);
    }
}
