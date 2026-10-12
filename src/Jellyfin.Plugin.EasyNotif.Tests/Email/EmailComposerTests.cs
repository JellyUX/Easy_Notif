using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Recap;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="EmailComposer"/>: newsletters route to the real digest with a "should send"
/// verdict, an empty digest still renders (for the preview) but is flagged not to send, the digest
/// window defaults to the last seven days, and other campaign types keep the placeholder body.
/// </summary>
public sealed class EmailComposerTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public Mock<INewsletterDigestService> Digest { get; } = new();

        public Mock<IWeeklyRecapService> Recap { get; } = new();

        /// <summary>The access filter; lets everything through unless a test narrows it.</summary>
        public Mock<IAccessFilter> Access { get; } = new();

        public FakeEasyNotifLog Log { get; } = new();

        public FakeConfigAccessor Config { get; }

        public FakeSecretStore Secrets { get; }

        public EmailComposer Composer { get; }

        public Harness(PluginConfiguration? config = null)
        {
            Config = new FakeConfigAccessor(config ?? new PluginConfiguration());
            Secrets = new FakeSecretStore(unsubscribeSecret: Config.Config.UnsubscribeSecret);
            Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
                .ReturnsAsync(new NewsletterDigest([], []));
            Recap.Setup(r => r.BuildFor(It.IsAny<Guid>(), It.IsAny<DateTime>()))
                .Returns((Guid id, DateTime _) => new RecapModel([], 0, 0, null, id.ToString("N")[..4]));
            Access.Setup(a => a.VisibleTo(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>()))
                .Returns((Guid _, IReadOnlyCollection<Guid> ids) => ids.ToHashSet());
            Composer = new EmailComposer(
                Digest.Object,
                Access.Object,
                new NewsletterComposer(TestTemplateStore.Create()),
                Recap.Object,
                new WeeklyRecapComposer(TestTemplateStore.Create()),
                new ServerLinkContext("srv", "Home"),
                Config,
                Secrets,
                Log);
        }
    }

    private static Campaign Newsletter(DateTime? lastSent = null) => new()
    {
        Id = "newsletter",
        Type = CampaignType.Newsletter,
        Category = EmailCategory.News,
        Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Friday, new TimeOnly(9, 0)),
        MailLanguage = "fr",
        LastSentUtc = lastSent
    };

    private static readonly Recipient Anyone = new(Guid.NewGuid(), "x@example.org");

    [Fact]
    public async Task Newsletter_NonEmptyDigest_ShouldSend_RendersNewsletter()
    {
        var h = new Harness();
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>())).ReturnsAsync(
            new NewsletterDigest([new DigestMovie(Guid.NewGuid(), "Dune", 2021, null, [], null, null, null)], []));

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);

        Assert.True(prepared.ShouldSend);
        Assert.Equal(1, prepared.MovieCount);
        var content = prepared.Render(Anyone)!;
        Assert.Contains("Dune", content.Html!, StringComparison.Ordinal);
        Assert.Contains(h.Log.Entries, e => e.EventType == "newsletter.digest");
    }

    [Fact]
    public async Task Newsletter_MarkAnnounced_RecordsTheDigestKeysAtTheGivenInstant()
    {
        var h = new Harness();
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>())).ReturnsAsync(
            new NewsletterDigest([new DigestMovie(Guid.NewGuid(), "Dune", 2021, null, [], null, null, null)], []) { Keys = ["movie:tmdb:438631"] });

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);
        prepared.MarkAnnounced!(Now);

        h.Digest.Verify(d => d.MarkAnnounced(
            It.Is<IEnumerable<string>>(k => k.SequenceEqual(new[] { "movie:tmdb:438631" })), Now), Times.Once);
    }

    [Fact]
    public async Task Newsletter_EmptyDigest_DoesNotSend_ButStillRenders()
    {
        var prepared = await new Harness().Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);

        Assert.False(prepared.ShouldSend);
        Assert.Null(prepared.MarkAnnounced);
        Assert.Equal("empty-digest", prepared.SkipReason);
        var content = prepared.Render(Anyone)!;
        Assert.Contains("Rien de neuf", content.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newsletter_NoLastSent_WindowStartsSevenDaysBack()
    {
        var h = new Harness();

        await h.Composer.PrepareAsync(Newsletter(lastSent: null), Now, CancellationToken.None);

        h.Digest.Verify(d => d.GetNewSinceAsync(Now.AddDays(-7), It.IsAny<CancellationToken>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Newsletter_WithLastSent_UsesThatAsTheWindowStart()
    {
        var h = new Harness();
        var lastSent = Now.AddDays(-3);

        await h.Composer.PrepareAsync(Newsletter(lastSent), Now, CancellationToken.None);

        h.Digest.Verify(d => d.GetNewSinceAsync(lastSent, It.IsAny<CancellationToken>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Newsletter_LastSentMonthsAgo_WindowIsCappedAtFourteenDays()
    {
        var h = new Harness();

        await h.Composer.PrepareAsync(Newsletter(Now.AddDays(-90)), Now, CancellationToken.None);

        h.Digest.Verify(d => d.GetNewSinceAsync(Now.AddDays(-14), It.IsAny<CancellationToken>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Newsletter_CapIsFilledWithTheRecipientsVisibleItems()
    {
        var h = new Harness(new PluginConfiguration { NewsletterMaxMovies = 2, PublicServerUrl = "https://media.example.org" });
        var movies = Enumerable.Range(1, 5)
            .Select(i => new DigestMovie(Guid.NewGuid(), $"Movie {i}", 2020, null, [], null, null, null))
            .ToList();
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new NewsletterDigest(movies, []));
        var hidden = movies[0].Id;
        h.Access.Setup(a => a.VisibleTo(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns((Guid _, IReadOnlyCollection<Guid> ids) => ids.Where(id => id != hidden).ToHashSet());

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);
        var html = prepared.Render(Anyone)!.Html!;

        Assert.DoesNotContain("Movie 1", html, StringComparison.Ordinal);
        Assert.Contains("Movie 2", html, StringComparison.Ordinal);
        Assert.Contains("Movie 3", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Movie 4", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"https://media.example.org/web/\" style=\"color:#6b7280;\">et 2 autres films</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newsletter_FreshWindow_IgnoresLastSent_AndLooksBackSevenDays()
    {
        var h = new Harness();

        await h.Composer.PrepareAsync(Newsletter(lastSent: Now.AddMinutes(-1)), Now, CancellationToken.None, freshWindow: true);

        h.Digest.Verify(d => d.GetNewSinceAsync(Now.AddDays(-7), It.IsAny<CancellationToken>(), It.IsAny<int>()), Times.Once);
    }

    private static Campaign Recap() => new()
    {
        Id = "weekly-recap",
        Type = CampaignType.WeeklyRecap,
        Category = EmailCategory.Recap,
        Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(8, 0)),
        MailLanguage = "fr"
    };

    [Fact]
    public async Task WeeklyRecap_AlwaysSends_AndRendersPerRecipient()
    {
        var h = new Harness();
        var seen = new List<Guid>();
        h.Recap.Setup(r => r.BuildFor(It.IsAny<Guid>(), It.IsAny<DateTime>()))
            .Returns((Guid id, DateTime _) =>
            {
                seen.Add(id);
                return new RecapModel([], id.GetHashCode() & 7, 0, null, id.ToString("N")[..4]);
            });

        var prepared = await h.Composer.PrepareAsync(Recap(), Now, CancellationToken.None);
        Assert.True(prepared.ShouldSend);

        var a = new Recipient(Guid.NewGuid(), "a@example.org");
        var b = new Recipient(Guid.NewGuid(), "b@example.org");
        var contentA = prepared.Render(a)!;
        var contentB = prepared.Render(b)!;

        Assert.Equal(new[] { a.UserId, b.UserId }, seen);
        Assert.NotEqual(contentA.Html, contentB.Html);
    }

    [Fact]
    public async Task WeeklyRecap_FreshWindow_HasNoSpecialEffect()
    {
        var h = new Harness();

        var prepared = await h.Composer.PrepareAsync(Recap(), Now, CancellationToken.None, freshWindow: true);
        prepared.Render(Anyone);

        h.Recap.Verify(r => r.BuildFor(Anyone.UserId, Now), Times.Once);
    }

    private static Harness WithPublicUrl() => new(new PluginConfiguration
    {
        PublicServerUrl = "https://media.example.org",
        UnsubscribeSecret = "unsub-secret-0123456789"
    });

    [Fact]
    public async Task Newsletter_EmbedsAPerRecipientUnsubscribeLink()
    {
        var h = WithPublicUrl();
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>())).ReturnsAsync(
            new NewsletterDigest([new DigestMovie(Guid.NewGuid(), "Dune", 2021, null, [], null, null, null)], []));

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);
        var a = prepared.Render(new Recipient(Guid.NewGuid(), "a@example.org"))!;
        var b = prepared.Render(new Recipient(Guid.NewGuid(), "b@example.org"))!;

        Assert.Contains("/EasyNotif/u/", a.Html!, StringComparison.Ordinal);
        Assert.Contains("?lang=fr", a.Html!, StringComparison.Ordinal);
        Assert.NotEqual(a.Html, b.Html); // different tokens
    }

    private static NewsletterDigest RestrictedDigest(Guid allowedMovie, Guid forbiddenMovie, Guid allowedEpisode, Guid forbiddenEpisode)
        => new(
            [
                new DigestMovie(allowedMovie, "Dune", 2021, null, [], null, null, null),
                new DigestMovie(forbiddenMovie, "Forbidden Film", 2024, null, [], "R", null, null)
            ],
            [
                new DigestSeries(Guid.NewGuid(), "Mixed Show", 2, [1, 2], null, null, null,
                    [new DigestEpisode(allowedEpisode, 1), new DigestEpisode(forbiddenEpisode, 2)]),
                new DigestSeries(Guid.NewGuid(), "Severance", 1, [1], null, null, null,
                    [new DigestEpisode(Guid.NewGuid(), 1)])
            ]);

    [Fact]
    public async Task Newsletter_RestrictedRecipient_GetsOnlyVisibleItems()
    {
        var h = new Harness();
        var (allowedMovie, forbiddenMovie, allowedEpisode, forbiddenEpisode) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(RestrictedDigest(allowedMovie, forbiddenMovie, allowedEpisode, forbiddenEpisode));
        var kid = new Recipient(Guid.NewGuid(), "kid@example.org");
        h.Access.Setup(a => a.VisibleTo(kid.UserId, It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns(new HashSet<Guid> { allowedMovie, allowedEpisode });

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);
        var content = prepared.Render(kid)!;

        Assert.Contains("Dune", content.Html!, StringComparison.Ordinal);
        Assert.Contains("Mixed Show", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Forbidden Film", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Severance", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Forbidden Film", content.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain("Severance", content.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newsletter_RecipientSeeingNothing_RendersNull()
    {
        var h = new Harness();
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(RestrictedDigest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        h.Access.Setup(a => a.VisibleTo(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns(new HashSet<Guid>());

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);

        Assert.True(prepared.ShouldSend);
        Assert.Null(prepared.Render(Anyone));
    }

    [Fact]
    public async Task Newsletter_AdminPreview_IsNotFiltered()
    {
        var h = new Harness();
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(RestrictedDigest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        h.Access.Setup(a => a.VisibleTo(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>()))
            .Returns(new HashSet<Guid>());

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None, freshWindow: true);
        var content = prepared.Render(new Recipient(Guid.Empty, "admin@example.org"))!;

        Assert.Contains("Forbidden Film", content.Html!, StringComparison.Ordinal);
        Assert.Contains("Severance", content.Html!, StringComparison.Ordinal);
        h.Access.Verify(a => a.VisibleTo(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
    }

    [Fact]
    public async Task Newsletter_Preview_HasNoUnsubscribeLink()
    {
        var h = WithPublicUrl();

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None, freshWindow: true);

        Assert.DoesNotContain("/EasyNotif/u/", prepared.Render(new Recipient(Guid.Empty, "admin@example.org"))!.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recap_NoPublicUrl_HasNoUnsubscribeLink()
    {
        var h = new Harness();

        var prepared = await h.Composer.PrepareAsync(Recap(), Now, CancellationToken.None);

        Assert.DoesNotContain("/EasyNotif/u/", prepared.Render(new Recipient(Guid.NewGuid(), "x@example.org"))!.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newsletter_RecipientWithoutTheShowsLibrary_GetsNoSeries()
    {
        var h = new Harness();
        var (films, shows) = (Guid.NewGuid(), Guid.NewGuid());
        var (movie, other, episode, otherEpisode) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var baseDigest = RestrictedDigest(movie, other, episode, otherEpisode);
        var digest = baseDigest with
        {
            ItemLibraries = new Dictionary<Guid, IReadOnlyList<Guid>>
            {
                [movie] = [films],
                [other] = [films],
                [episode] = [shows],
                [otherEpisode] = [shows],
                [baseDigest.Series[1].EpisodeList[0].Id] = [shows]
            }
        };
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(digest);
        var filmsOnly = new Recipient(Guid.NewGuid(), "films@example.org", [shows]);

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);
        var content = prepared.Render(filmsOnly)!;
        var everything = prepared.Render(Anyone)!;

        Assert.Contains("Dune", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Mixed Show", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Severance", content.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Mixed Show", content.Text!, StringComparison.Ordinal);
        Assert.Contains("Severance", everything.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newsletter_RecipientExcludingEveryLibraryOfTheDigest_RendersNull()
    {
        var h = new Harness();
        var lib = Guid.NewGuid();
        var digest = new NewsletterDigest([new DigestMovie(Guid.NewGuid(), "Dune", 2021, null, [], null, null, null)], []);
        digest = digest with { ItemLibraries = new Dictionary<Guid, IReadOnlyList<Guid>> { [digest.Movies[0].Id] = [lib] } };
        h.Digest.Setup(d => d.GetNewSinceAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(digest);

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);

        Assert.Null(prepared.Render(new Recipient(Guid.NewGuid(), "x@example.org", [lib])));
    }
}
