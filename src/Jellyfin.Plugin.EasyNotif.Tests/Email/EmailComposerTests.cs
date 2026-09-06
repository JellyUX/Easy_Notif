using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
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

        public FakeEasyNotifLog Log { get; } = new();

        public EmailComposer Composer { get; }

        public Harness()
        {
            Digest.Setup(d => d.GetNewSince(It.IsAny<DateTime>(), It.IsAny<int>()))
                .Returns(new NewsletterDigest([], []));
            Composer = new EmailComposer(
                Digest.Object,
                new NewsletterComposer(new TemplateStore()),
                new ServerLinkContext("srv", "Home"),
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
        h.Digest.Setup(d => d.GetNewSince(It.IsAny<DateTime>(), It.IsAny<int>())).Returns(
            new NewsletterDigest([new DigestMovie(Guid.NewGuid(), "Dune", 2021, null, [], null, null, null)], []));

        var prepared = await h.Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);

        Assert.True(prepared.ShouldSend);
        Assert.Equal(1, prepared.MovieCount);
        var content = prepared.Render(Anyone);
        Assert.Contains("Dune", content.Html!, StringComparison.Ordinal);
        Assert.Contains(h.Log.Entries, e => e.EventType == "newsletter.digest");
    }

    [Fact]
    public async Task Newsletter_EmptyDigest_DoesNotSend_ButStillRenders()
    {
        var prepared = await new Harness().Composer.PrepareAsync(Newsletter(), Now, CancellationToken.None);

        Assert.False(prepared.ShouldSend);
        Assert.Equal("empty-digest", prepared.SkipReason);
        var content = prepared.Render(Anyone);
        Assert.Contains("Rien de neuf", content.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newsletter_NoLastSent_WindowStartsSevenDaysBack()
    {
        var h = new Harness();

        await h.Composer.PrepareAsync(Newsletter(lastSent: null), Now, CancellationToken.None);

        h.Digest.Verify(d => d.GetNewSince(Now.AddDays(-7), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Newsletter_WithLastSent_UsesThatAsTheWindowStart()
    {
        var h = new Harness();
        var lastSent = Now.AddDays(-3);

        await h.Composer.PrepareAsync(Newsletter(lastSent), Now, CancellationToken.None);

        h.Digest.Verify(d => d.GetNewSince(lastSent, It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task WeeklyRecap_KeepsThePlaceholder()
    {
        var campaign = Newsletter();
        campaign.Type = CampaignType.WeeklyRecap;

        var prepared = await new Harness().Composer.PrepareAsync(campaign, Now, CancellationToken.None);

        Assert.True(prepared.ShouldSend);
        Assert.Contains("Placeholder", prepared.Render(Anyone).Html!, StringComparison.Ordinal);
    }
}
