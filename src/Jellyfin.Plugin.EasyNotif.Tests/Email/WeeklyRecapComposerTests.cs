using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Recap;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Storage;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="WeeklyRecapComposer.Compose"/>: the HTML lists the week's entries and the
/// year total, a quiet week swaps in the calm-week copy and subject, <c>PartialSince</c> shows the
/// "since installation" line, and the salutation uses the username when present.
/// </summary>
public sealed class WeeklyRecapComposerTests
{
    private readonly WeeklyRecapComposer _composer = new(new TemplateStore());

    private static Campaign Campaign(string lang = "fr") => new()
    {
        Id = "weekly-recap",
        Type = CampaignType.WeeklyRecap,
        Category = EmailCategory.Recap,
        Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(8, 0)),
        MailLanguage = lang
    };

    private static RecapModel WithActivity(DateTime? partialSince = null, string? name = "bob") => new(
        [
            new WatchedEntry(RecapKind.Series, "The Wire", null, 3, new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc), true),
            new WatchedEntry(RecapKind.Movie, "Heat", null, 1, new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc), true)
        ],
        YearTotal: 20,
        YearCompleted: 15,
        PartialSince: partialSince,
        UserName: name);

    [Fact]
    public void RendersEntries_TheGroupLine_AndTheYearTotal()
    {
        var content = _composer.Compose(Campaign(), WithActivity(), "Home");

        Assert.Contains("The Wire", content.Html!, StringComparison.Ordinal);
        Assert.Contains("3 episodes", content.Html!, StringComparison.Ordinal);
        Assert.Contains("Heat", content.Html!, StringComparison.Ordinal);
        Assert.Contains("15", content.Html!, StringComparison.Ordinal);
        Assert.Contains("20", content.Html!, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(content.Text));
        Assert.DoesNotContain("<", content.Text!, StringComparison.Ordinal);
    }

    [Fact]
    public void SubjectDiffersByLanguage()
    {
        Assert.StartsWith("Ton resume", _composer.Compose(Campaign("fr"), WithActivity(), "Home").Subject, StringComparison.Ordinal);
        Assert.StartsWith("Your week", _composer.Compose(Campaign("en"), WithActivity(), "Home").Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void QuietWeek_UsesTheCalmCopy_AndSubject_NoList()
    {
        var model = new RecapModel([], 5, 4, null, "bob");

        var content = _composer.Compose(Campaign(), model, "Home");

        Assert.Contains("semaine calme", content.Subject, StringComparison.Ordinal);
        Assert.Contains("Rien vu cette semaine", content.Html!, StringComparison.Ordinal);
        Assert.Contains("4", content.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialSince_AddsTheSinceInstallationLine_WhenSet()
    {
        var withDate = _composer.Compose(Campaign(), WithActivity(partialSince: new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc)), "Home");
        Assert.Contains("2026-03-15", withDate.Html!, StringComparison.Ordinal);
        Assert.Contains("installation", withDate.Html!, StringComparison.Ordinal);

        var without = _composer.Compose(Campaign(), WithActivity(partialSince: null), "Home");
        Assert.DoesNotContain("installation d'Easy Notif", without.Html!, StringComparison.Ordinal);
    }

    [Fact]
    public void Salutation_UsesTheUserName_WhenPresent()
    {
        Assert.Contains("Bonjour bob", _composer.Compose(Campaign(), WithActivity(name: "bob"), "Home").Html!, StringComparison.Ordinal);

        var anon = _composer.Compose(Campaign(), WithActivity(name: null), "Home");
        Assert.Contains("Bonjour", anon.Html!, StringComparison.Ordinal);
        Assert.DoesNotContain("Bonjour bob", anon.Html!, StringComparison.Ordinal);
    }
}
