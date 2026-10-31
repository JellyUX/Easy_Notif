using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="CampaignStore"/>: the two system campaigns are seeded disabled with the right
/// default schedules, seeding is idempotent (it never clobbers an edited row), <see cref="CampaignStore.Update"/>
/// is write-through, an unknown id is a no-op, a corrupt file self-heals, and a non-default schedule
/// round-trips through JSON.
/// </summary>
public sealed class CampaignStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-campaignstore-tests-" + Guid.NewGuid());
    private readonly List<CampaignStore> _stores = [];

    private string DataDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif");

    private string FilePath => Path.Combine(DataDir, "campaigns.json");

    private CampaignStore Build(ILogger<CampaignStore>? logger = null, IFileSystem? fileSystem = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new CampaignStore(paths.Object, fileSystem ?? new FileSystem(), logger ?? NullLogger<CampaignStore>.Instance);
        _stores.Add(store);
        return store;
    }

    private void SeedFile(string json)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, json);
    }

    private const string NewerSchemaFile = """
        {
          "Schema": 99,
          "Campaigns": [
            {
              "Id": "newsletter",
              "Type": "Newsletter",
              "Category": "News",
              "TemplateId": "newsletter",
              "Schedule": { "Kind": "EveryNDays", "Time": "09:00:00", "IntervalDays": 0 },
              "MailLanguage": "fr",
              "Enabled": true
            }
          ],
          "FutureProperty": [1, 2]
        }
        """;

    [Fact]
    public void NewerSchema_ConstructionDoesNotThrow_AndNeverTouchesTheFile()
    {
        SeedFile(NewerSchemaFile);

        var store = Build();

        Assert.True(store.IsReadOnly);
        Assert.Equal(2, store.All().Count);
        Assert.Throws<StoreReadOnlyException>(() => store.Update("newsletter", c => c.MailLanguage = "en"));
        Assert.Equal(NewerSchemaFile, File.ReadAllText(FilePath));
        Assert.Equal(["campaigns.json"], Directory.GetFiles(DataDir).Select(Path.GetFileName));
    }

    [Fact]
    public void NewerSchema_EnabledCampaignWithAnInvalidSchedule_IsDisabledInMemoryOnly()
    {
        SeedFile(NewerSchemaFile);

        var store = Build();

        Assert.False(store.Get("newsletter")!.Enabled);
        Assert.Equal(NewerSchemaFile, File.ReadAllText(FilePath));
    }

    [Fact]
    public void UnwritableDirectory_FreshInstall_ConstructionDoesNotThrow_AndSeedsInMemory()
    {
        var store = Build(fileSystem: new UnwritableFileSystem());

        Assert.False(store.IsReadOnly);
        Assert.Equal(2, store.All().Count);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Construction_NeverWrites_TheRepairIsPersistedByTheNextUpdate()
    {
        var store = Build();
        Assert.False(File.Exists(FilePath));

        store.Update("does-not-exist", c => c.Enabled = true);

        var reloaded = File.ReadAllText(FilePath);
        Assert.Contains("\"newsletter\"", reloaded, StringComparison.Ordinal);
        Assert.Contains("\"weekly-recap\"", reloaded, StringComparison.Ordinal);
    }

    [Fact]
    public void Construction_SeedsTheTwoSystemCampaigns_Disabled_WithDefaultSchedules()
    {
        var all = Build().All();

        Assert.Equal(2, all.Count);

        var newsletter = all.Single(c => c.Id == "newsletter");
        Assert.Equal(CampaignType.Newsletter, newsletter.Type);
        Assert.Equal(EmailCategory.News, newsletter.Category);
        Assert.False(newsletter.Enabled);
        Assert.Equal(RecurrenceKind.Weekly, newsletter.Schedule.Kind);
        Assert.Equal(DayOfWeek.Friday, newsletter.Schedule.DayOfWeek);
        Assert.Equal(new TimeOnly(9, 0), newsletter.Schedule.Time);

        var recap = all.Single(c => c.Id == "weekly-recap");
        Assert.Equal(CampaignType.WeeklyRecap, recap.Type);
        Assert.Equal(EmailCategory.Recap, recap.Category);
        Assert.False(recap.Enabled);
        Assert.Equal(DayOfWeek.Monday, recap.Schedule.DayOfWeek);
        Assert.Equal(new TimeOnly(8, 0), recap.Schedule.Time);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    [InlineData(int.MaxValue)]
    public void Load_EnabledCampaignWithAnInvalidInterval_IsDisabledAndPersisted(int intervalDays)
    {
        Build().Update("newsletter", c =>
        {
            c.Enabled = true;
            c.Schedule = RecurrenceSchedule.EveryNDays(1, new TimeOnly(9, 0)) with { IntervalDays = intervalDays };
        });

        var logger = new Mock<ILogger<CampaignStore>>();
        var repaired = Build(logger.Object);
        Assert.False(repaired.Get("newsletter")!.Enabled);
        logger.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // Construction never writes: the repair reaches the disk with the next update, of any row.
        repaired.Update("weekly-recap", c => c.MailLanguage = "en");

        // Persisted: a third load reads it disabled without warning again.
        var quiet = new Mock<ILogger<CampaignStore>>();
        Assert.False(Build(quiet.Object).Get("newsletter")!.Enabled);
        quiet.Verify(
            l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), null, It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public void Load_EnabledCampaignWithDayOfMonthZero_IsDisabled()
    {
        Build().Update("newsletter", c =>
        {
            c.Enabled = true;
            c.Schedule = RecurrenceSchedule.Monthly(1, new TimeOnly(9, 0)) with { DayOfMonth = 0 };
        });

        Assert.False(Build().Get("newsletter")!.Enabled);
    }

    [Fact]
    public void Load_EnabledCampaignWithAValidSchedule_StaysEnabled()
    {
        Build().Update("newsletter", c =>
        {
            c.Enabled = true;
            c.Schedule = RecurrenceSchedule.EveryNDays(365, new TimeOnly(9, 0));
        });

        Assert.True(Build().Get("newsletter")!.Enabled);
    }

    [Fact]
    public void AllAndGet_ReturnDetachedCopies()
    {
        var store = Build();

        store.Get("newsletter")!.Enabled = true;
        store.All().Single(c => c.Id == "newsletter").NextRunUtc = DateTime.UtcNow;

        var reread = store.Get("newsletter")!;
        Assert.False(reread.Enabled);
        Assert.Null(reread.NextRunUtc);
    }

    [Fact]
    public void Seeding_IsIdempotent_AndDoesNotClobberAnEditedRow()
    {
        Build().Update("newsletter", c =>
        {
            c.Enabled = true;
            c.MailLanguage = "en";
            c.Schedule = RecurrenceSchedule.Daily(new TimeOnly(6, 30));
        });

        var reloaded = Build().All();

        Assert.Equal(2, reloaded.Count);
        var newsletter = reloaded.Single(c => c.Id == "newsletter");
        Assert.True(newsletter.Enabled);
        Assert.Equal("en", newsletter.MailLanguage);
        Assert.Equal(RecurrenceKind.Daily, newsletter.Schedule.Kind);
        Assert.Equal(new TimeOnly(6, 30), newsletter.Schedule.Time);
    }

    [Fact]
    public void Update_IsWriteThrough()
    {
        Build().Update("weekly-recap", c => c.NextRunUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var reloaded = Build().Get("weekly-recap");

        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), reloaded!.NextRunUtc);
    }

    [Fact]
    public void Update_UnknownId_ReturnsFalse_LeavesFileUnchanged()
    {
        Build().Update("newsletter", c => c.MailLanguage = "en");
        var store = Build();
        var before = File.ReadAllText(FilePath);

        var updated = store.Update("does-not-exist", c => c.Enabled = true);

        Assert.False(updated);
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Construction_WhenFileIsCorrupt_ReSeeds_BacksUpOriginal_LogsError()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, "{ not json");
        var logger = new Mock<ILogger<CampaignStore>>();

        var store = Build(logger.Object);

        Assert.Equal(2, store.All().Count);
        Assert.Single(Directory.GetFiles(DataDir, "campaigns.json.corrupt-*"));
        logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void NonDefaultSchedule_RoundTripsThroughJson()
    {
        Build().Update("newsletter", c => c.Schedule = RecurrenceSchedule.Monthly(15, new TimeOnly(7, 30)));

        var reloaded = Build().Get("newsletter")!.Schedule;

        Assert.Equal(RecurrenceKind.Monthly, reloaded.Kind);
        Assert.Equal(15, reloaded.DayOfMonth);
        Assert.Equal(new TimeOnly(7, 30), reloaded.Time);
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A data directory the process can read but not write (permissions, read-only mount).</summary>
    private sealed class UnwritableFileSystem : IFileSystem
    {
        private readonly FileSystem _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents) => throw new UnauthorizedAccessException(path);

        public void Move(string sourceFileName, string destFileName, bool overwrite) => throw new UnauthorizedAccessException(destFileName);

        public void Delete(string path) => throw new UnauthorizedAccessException(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern) => _inner.EnumerateFiles(path, searchPattern);
    }
}
