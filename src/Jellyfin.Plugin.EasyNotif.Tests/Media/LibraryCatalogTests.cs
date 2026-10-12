using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="LibraryCatalog"/> (FEAT-02): an item's libraries are its collection folders, and
/// an unknown user or a Jellyfin error gives an empty answer with a warning, never an exception. The
/// user's visible libraries are measured in the test container (the user root folder is not mockable).
/// </summary>
public sealed class LibraryCatalogTests
{
    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly FakeEasyNotifLog _log = new();

    private LibraryCatalog Catalog() => new(_users.Object, _library.Object, _log);

    [Fact]
    public void UnknownUser_HasNoLibrary()
    {
        Assert.Empty(Catalog().NewsletterLibrariesFor(Guid.NewGuid()));
        _library.Verify(l => l.GetUserRootFolder(), Times.Never);
    }

    [Fact]
    public void LibrariesOf_ReturnsTheCollectionFolderIds()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var (a, b) = (new Folder { Id = Guid.NewGuid() }, new Folder { Id = Guid.NewGuid() });
        _library.Setup(l => l.GetCollectionFolders(movie)).Returns([a, b]);

        Assert.Equal([a.Id, b.Id], Catalog().LibrariesOf(movie));
    }

    [Fact]
    public void LibrariesOf_JellyfinError_IsEmpty_AndWarns()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        _library.Setup(l => l.GetCollectionFolders(movie)).Throws(new InvalidOperationException("boom"));

        Assert.Empty(Catalog().LibrariesOf(movie));
        Assert.Contains(_log.Entries, e => e.Level == "Warn" && e.EventType == "libraries.error");
    }
}
