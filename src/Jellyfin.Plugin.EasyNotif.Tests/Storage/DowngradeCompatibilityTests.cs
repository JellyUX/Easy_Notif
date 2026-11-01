using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Rollback guard for the file format contract (audit OPS-03): a file written by this version must
/// be read by 1.0.2 without loss. 1.0.2 parsed every store with the default
/// <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions?)"/> options, which this
/// test reproduces against frozen copies of the 1.0.2 models (<see cref="V1_0_2"/>). A failure here
/// means a change is not additive and would push a downgraded install into <c>.corrupt-*</c> recovery.
/// </summary>
public sealed class DowngradeCompatibilityTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "enotif-downgrade-tests-" + Guid.NewGuid());

    private string DataDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void CampaignsFile_WrittenNow_IsReadBy102WithoutLoss()
    {
        var next = new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc);
        using (var store = new CampaignStore(Paths(), new FileSystem(), NullLogger<CampaignStore>.Instance))
        {
            store.Update(CampaignStore.NewsletterId, c =>
            {
                c.Enabled = true;
                c.Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(9, 30));
                c.MailLanguage = "en";
                c.NextRunUtc = next;
                c.LastError = "IOException: disk";
                c.LastErrorUtc = next.AddDays(-1);
                c.PauseNextRun = true;
            });
        }

        var old = JsonSerializer.Deserialize<V1_0_2.CampaignFile>(File.ReadAllText(Path.Combine(DataDir, "campaigns.json")));

        Assert.NotNull(old);
        Assert.Equal(1, old.Schema);
        var newsletter = Assert.Single(old.Campaigns, c => c.Id == CampaignStore.NewsletterId);
        Assert.True(newsletter.Enabled);
        Assert.Equal(V1_0_2.CampaignType.Newsletter, newsletter.Type);
        Assert.Equal(V1_0_2.RecurrenceKind.Weekly, newsletter.Schedule.Kind);
        Assert.Equal(DayOfWeek.Monday, newsletter.Schedule.DayOfWeek);
        Assert.Equal(new TimeOnly(9, 30), newsletter.Schedule.Time);
        Assert.Equal("en", newsletter.MailLanguage);
        Assert.Equal(next, newsletter.NextRunUtc);
        Assert.Contains(old.Campaigns, c => c.Id == CampaignStore.WeeklyRecapId);
    }

    [Fact]
    public void PreferencesFile_WrittenNow_IsReadBy102WithoutLoss()
    {
        var userId = Guid.NewGuid();
        var updated = new DateTime(2026, 10, 6, 18, 0, 0, DateTimeKind.Utc);
        using (var store = new PreferencesStore(Paths(), new FileSystem(), NullLogger<PreferencesStore>.Instance))
        {
            store.Mutate(users =>
            {
                users.Add(new UserPreference
                {
                    UserId = userId,
                    ContactEmail = "a@b.co",
                    Categories = { [EmailCategory.News] = true, [EmailCategory.Recap] = false },
                    AnnouncementsOptOut = true,
                    ExcludedLibraries = [Guid.NewGuid()],
                    UpdatedAt = updated
                });
                return true;
            });
        }

        var old = JsonSerializer.Deserialize<V1_0_2.PreferencesFile>(File.ReadAllText(Path.Combine(DataDir, "preferences.json")));

        Assert.NotNull(old);
        Assert.Equal(1, old.Schema);
        var user = Assert.Single(old.Users);
        Assert.Equal(userId, user.UserId);
        Assert.Equal("a@b.co", user.ContactEmail);
        Assert.True(user.Categories[V1_0_2.EmailCategory.News]);
        Assert.False(user.Categories[V1_0_2.EmailCategory.Recap]);
        Assert.Equal(updated, user.UpdatedAt);
    }

    private IApplicationPaths Paths()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        return paths.Object;
    }

    /// <summary>Frozen data shape of the on-disk models at tag v1.0.2. Never update these.</summary>
    public static class V1_0_2
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum EmailCategory
        {
            News,
            Recap
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum CampaignType
        {
            Newsletter,
            WeeklyRecap
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum RecurrenceKind
        {
            Daily,
            Weekly,
            Monthly,
            EveryNDays
        }

        public sealed record RecurrenceSchedule
        {
            public required RecurrenceKind Kind { get; init; }

            public required TimeOnly Time { get; init; }

            [JsonConverter(typeof(JsonStringEnumConverter<DayOfWeek>))]
            public DayOfWeek? DayOfWeek { get; init; }

            public int? DayOfMonth { get; init; }

            public int? IntervalDays { get; init; }
        }

        public sealed class Campaign
        {
            public string Id { get; set; } = string.Empty;

            public CampaignType Type { get; set; }

            public EmailCategory Category { get; set; }

            public RecurrenceSchedule Schedule { get; set; } = new() { Kind = RecurrenceKind.Daily, Time = new TimeOnly(9, 0) };

            public string TemplateId { get; set; } = string.Empty;

            public string MailLanguage { get; set; } = "fr";

            public bool Enabled { get; set; }

            public DateTime? LastSentUtc { get; set; }

            public DateTime? NextRunUtc { get; set; }
        }

        public sealed class CampaignFile
        {
            public int Schema { get; set; } = 1;

            public List<Campaign> Campaigns { get; init; } = [];
        }

        public sealed class UserPreference
        {
            public Guid UserId { get; set; }

            public string? ContactEmail { get; set; }

            public Dictionary<EmailCategory, bool> Categories { get; init; } = [];

            public DateTime UpdatedAt { get; set; }
        }

        public sealed class PreferencesFile
        {
            public int Schema { get; set; } = 1;

            public List<UserPreference> Users { get; set; } = [];
        }
    }
}
