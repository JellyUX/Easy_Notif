using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Services;

/// <summary>
/// Covers <see cref="PreferenceService"/>: the opt-out default, contact-address validation,
/// category writes, recipient resolution (opted in AND valid address), and the admin table
/// (left join over every Jellyfin user, addresses masked).
/// </summary>
public sealed class PreferenceServiceTests
{
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    private readonly InMemoryPreferencesStore _store = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly PreferenceService _service;

    public PreferenceServiceTests()
    {
        _users.Setup(m => m.GetUsers()).Returns(
        [
            new User("alice", "Default", "Default") { Id = _alice },
            new User("bob", "Default", "Default") { Id = _bob }
        ]);
        _service = new PreferenceService(_store, _users.Object);
    }

    private static Dictionary<EmailCategory, bool> Cats(bool news = false, bool recap = false)
        => new() { [EmailCategory.News] = news, [EmailCategory.Recap] = recap };

    // -------------------------------------------------------------------------
    // Default: opted out of everything
    // -------------------------------------------------------------------------

    [Fact]
    public void GetOrCreate_UnknownUser_IsOptedOutOfEverything_WithNoEmail()
    {
        var pref = _service.GetOrCreate(_alice);

        Assert.Equal(_alice, pref.UserId);
        Assert.Null(pref.ContactEmail);
        Assert.False(pref.Categories.GetValueOrDefault(EmailCategory.News));
        Assert.False(pref.Categories.GetValueOrDefault(EmailCategory.Recap));
        Assert.Empty(_store.Rows); // a read never persists a default
    }

    // -------------------------------------------------------------------------
    // Contact address
    // -------------------------------------------------------------------------

    [Fact]
    public void SetContactEmail_Valid_IsStored()
    {
        _service.SetContactEmail(_alice, "  alice@example.org ");

        Assert.Equal("alice@example.org", _service.GetOrCreate(_alice).ContactEmail);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("a@")]
    [InlineData("@b")]
    public void SetContactEmail_Invalid_Throws(string email)
    {
        Assert.Throws<ArgumentException>(() => _service.SetContactEmail(_alice, email));
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public void SetContactEmail_NullOrBlank_ClearsIt()
    {
        _service.SetContactEmail(_alice, "alice@example.org");

        _service.SetContactEmail(_alice, "   ");

        Assert.Null(_service.GetOrCreate(_alice).ContactEmail);
    }

    [Fact]
    public void SetCategories_Persists()
    {
        _service.SetCategories(_alice, Cats(news: true));

        Assert.True(_service.GetOrCreate(_alice).Categories[EmailCategory.News]);
        Assert.False(_service.GetOrCreate(_alice).Categories.GetValueOrDefault(EmailCategory.Recap));
    }

    // -------------------------------------------------------------------------
    // Recipient resolution
    // -------------------------------------------------------------------------

    [Fact]
    public void GetRecipients_ExcludesUsersWithTheCategoryOff()
    {
        _service.SetContactEmail(_alice, "alice@example.org");
        _service.SetCategories(_alice, Cats(news: false, recap: true));

        Assert.Empty(_service.GetRecipients(EmailCategory.News));
    }

    [Fact]
    public void GetRecipients_ExcludesUsersOptedInButWithNoAddress()
    {
        _service.SetCategories(_alice, Cats(news: true));

        Assert.Empty(_service.GetRecipients(EmailCategory.News));
    }

    [Fact]
    public void GetRecipients_IncludesUsersOptedInWithAValidAddress()
    {
        _service.SetContactEmail(_alice, "alice@example.org");
        _service.SetCategories(_alice, Cats(news: true));
        _service.SetContactEmail(_bob, "bob@example.org");
        _service.SetCategories(_bob, Cats(news: false));

        var recipients = _service.GetRecipients(EmailCategory.News);

        var only = Assert.Single(recipients);
        Assert.Equal(_alice, only.UserId);
        Assert.Equal("alice@example.org", only.Email);
    }

    // -------------------------------------------------------------------------
    // Admin table
    // -------------------------------------------------------------------------

    [Fact]
    public void GetAllForAdmin_IncludesEveryUser_EvenWithoutAPreferenceRow()
    {
        _service.SetContactEmail(_alice, "alice@example.org");

        var rows = _service.GetAllForAdmin();

        Assert.Equal(2, rows.Count);
        var bobRow = Assert.Single(rows, r => r.UserId == _bob);
        Assert.False(bobRow.HasEmail);
        Assert.Equal("(none)", bobRow.MaskedEmail);
        Assert.Null(bobRow.UpdatedAt);
        Assert.False(bobRow.Categories[EmailCategory.News]);
        Assert.False(bobRow.Categories[EmailCategory.Recap]);
    }

    [Fact]
    public void GetAllForAdmin_MasksTheStoredAddress_NeverReturnsItInFull()
    {
        _service.SetContactEmail(_alice, "samuel.lecomte37@gmail.com");

        var aliceRow = Assert.Single(_service.GetAllForAdmin(), r => r.UserId == _alice);

        Assert.True(aliceRow.HasEmail);
        Assert.Equal("s***7@gmail.com", aliceRow.MaskedEmail);
        Assert.DoesNotContain("samuel.lecomte37", aliceRow.MaskedEmail, StringComparison.Ordinal);
    }

    [Fact]
    public void SetContactEmailAndCategories_WorkForAnyUserId_NotOnlyTheCaller()
    {
        // The service has no "self" notion; the caller/admin distinction is enforced at the
        // controller (identity for /me, RequiresElevation for /admin). Writing for another user id
        // must simply work.
        _service.SetContactEmail(_bob, "bob@example.org");
        _service.SetCategories(_bob, Cats(news: true, recap: true));

        var bob = _service.GetOrCreate(_bob);
        Assert.Equal("bob@example.org", bob.ContactEmail);
        Assert.True(bob.Categories[EmailCategory.News]);
        Assert.True(bob.Categories[EmailCategory.Recap]);
    }

    private sealed class InMemoryPreferencesStore : IPreferencesStore
    {
        public List<UserPreference> Rows { get; } = [];

        public IReadOnlyList<UserPreference> ReadAll() => [.. Rows];

        public void Mutate(Func<List<UserPreference>, bool> mutation) => mutation(Rows);
    }
}
