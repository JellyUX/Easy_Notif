using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="PreferencesStore"/> persistence: atomic writes, the no-op guard, empty-list
/// persistence, corrupt-file recovery, write-lock serialization, and the in-memory cache (R13:
/// no file read per call). Uses a real <see cref="FileSystem"/> against a per-test temp directory,
/// so the tests exercise the actual on-disk behaviour.
/// </summary>
public sealed class PreferencesStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "enotif-prefstore-tests-" + Guid.NewGuid());

    private readonly List<PreferencesStore> _stores = [];

    private string DataDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif");

    private string FilePath => Path.Combine(DataDir, "preferences.json");

    private PreferencesStore BuildStore(IFileSystem fileSystem, ILogger<PreferencesStore>? logger = null)
    {
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.Setup(p => p.DataPath).Returns(_tempDir);

        var store = new PreferencesStore(
            applicationPaths.Object,
            fileSystem,
            logger ?? NullLogger<PreferencesStore>.Instance);
        _stores.Add(store);
        return store;
    }

    private static UserPreference NewPref(Guid? userId = null, string? email = "a@b.co") => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        ContactEmail = email,
        Categories = { [EmailCategory.News] = true },
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public void Mutate_WhenMutationReturnsTrue_WritesJsonAtomically_NoStrayTempFile()
    {
        var store = BuildStore(new FileSystem());
        var pref = NewPref();

        store.Mutate(users =>
        {
            users.Add(pref);
            return true;
        });

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));

        var persisted = JsonSerializer.Deserialize<PreferencesFile>(File.ReadAllText(FilePath));
        Assert.NotNull(persisted);
        var stored = Assert.Single(persisted!.Users);
        Assert.Equal(pref.UserId, stored.UserId);
        Assert.Equal("a@b.co", stored.ContactEmail);
        Assert.True(stored.Categories[EmailCategory.News]);

        Assert.Single(store.ReadAll());
    }

    [Fact]
    public void Mutate_WhenMutationReturnsFalse_DoesNotWriteFile()
    {
        var store = BuildStore(new FileSystem());

        store.Mutate(_ => false);

        Assert.False(File.Exists(FilePath));
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void Mutate_ClearingToEmptyList_IsPersisted()
    {
        var store = BuildStore(new FileSystem());
        store.Mutate(users =>
        {
            users.Add(NewPref());
            return true;
        });

        store.Mutate(users =>
        {
            users.Clear();
            return true;
        });

        Assert.True(File.Exists(FilePath));
        Assert.Empty(store.ReadAll());
        Assert.Contains("\"Users\": []", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Construction_WhenFileIsCorrupt_StartsEmpty_BacksUpOriginal_LogsError()
    {
        var logger = new Mock<ILogger<PreferencesStore>>();
        Directory.CreateDirectory(DataDir);
        const string garbage = "{ not json";
        File.WriteAllText(FilePath, garbage);

        var store = BuildStore(new FileSystem(), logger.Object);

        Assert.Empty(store.ReadAll());
        Assert.False(File.Exists(FilePath));

        var backup = Assert.Single(Directory.GetFiles(DataDir, "preferences.json.corrupt-*"));
        Assert.Equal(garbage, File.ReadAllText(backup));

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Construction_WhenACorruptBackupAlreadyExists_KeepsOnlyTheNewest()
    {
        Directory.CreateDirectory(DataDir);
        // A leftover backup from an earlier corruption event.
        var stalePath = Path.Combine(DataDir, "preferences.json.corrupt-20200101000000");
        File.WriteAllText(stalePath, "old garbage");
        const string freshGarbage = "{ still not json";
        File.WriteAllText(FilePath, freshGarbage);

        BuildStore(new FileSystem());

        var backup = Assert.Single(Directory.GetFiles(DataDir, "preferences.json.corrupt-*"));
        Assert.False(File.Exists(stalePath));
        Assert.Equal(freshGarbage, File.ReadAllText(backup));
    }

    [Fact]
    public void ReadAll_ServesTheMemoryCache_WithoutReadingTheFileAgain()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(new PreferencesFile { Users = [NewPref()] }));

        var counting = new CountingFileSystem();
        var store = BuildStore(counting);

        // One read at construction; none afterwards, no matter how many callers ask.
        Assert.Single(store.ReadAll());
        Assert.Single(store.ReadAll());
        store.ReadAll();

        Assert.Equal(1, counting.ReadCount);

        // A mutation updates the cache in place - still no extra read.
        store.Mutate(users =>
        {
            users.Add(NewPref());
            return true;
        });
        Assert.Equal(2, store.ReadAll().Count);
        Assert.Equal(1, counting.ReadCount);
    }

    [Fact]
    public async Task Mutate_SecondWriterBlocksUntilFirstReleases_ThenSeesFirstWrite()
    {
        var blocking = new BlockingFileSystem();
        var store = BuildStore(blocking);

        var prefA = NewPref();
        var prefB = NewPref();

        var writerA = Task.Run(() => store.Mutate(users =>
        {
            users.Add(prefA);
            return true;
        }));

        Assert.True(
            blocking.WriteStarted.Wait(TimeSpan.FromSeconds(5)),
            "Writer A never reached the paused Move.");

        List<UserPreference>? bObserved = null;
        var writerB = Task.Run(() => store.Mutate(users =>
        {
            bObserved = [.. users];
            users.Add(prefB);
            return true;
        }));

        var bFinishedEarly = await Task.WhenAny(writerB, Task.Delay(200)) == writerB;
        Assert.False(bFinishedEarly, "Writer B should block on the write lock while A holds it.");

        blocking.ReleaseWrite.Set();

        await writerA;
        await writerB;

        Assert.NotNull(bObserved);
        Assert.Contains(bObserved!, p => p.UserId == prefA.UserId);
        Assert.Equal(2, store.ReadAll().Count);
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
            // Best-effort cleanup; a leftover temp dir under %TEMP% is harmless.
        }
    }

    /// <summary>Counts <see cref="ReadAllText"/> calls; everything else delegates to a real one.</summary>
    private sealed class CountingFileSystem : IFileSystem
    {
        private readonly FileSystem _inner = new();

        public int ReadCount { get; private set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public string ReadAllText(string path)
        {
            ReadCount++;
            return _inner.ReadAllText(path);
        }

        public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);

        public void Move(string sourceFileName, string destFileName, bool overwrite) =>
            _inner.Move(sourceFileName, destFileName, overwrite);

        public void Delete(string path) => _inner.Delete(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern) => _inner.EnumerateFiles(path, searchPattern);
    }

    /// <summary>
    /// Wraps a real <see cref="FileSystem"/> but pauses inside <see cref="Move"/> (the atomic
    /// rename) until <see cref="ReleaseWrite"/> is signalled, so a test can deterministically hold a
    /// writer inside the write lock instead of racing on timing.
    /// </summary>
    private sealed class BlockingFileSystem : IFileSystem
    {
        private readonly FileSystem _inner = new();

        public ManualResetEventSlim WriteStarted { get; } = new(initialState: false);

        public ManualResetEventSlim ReleaseWrite { get; } = new(initialState: false);

        public bool FileExists(string path) => _inner.FileExists(path);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            WriteStarted.Set();
            ReleaseWrite.Wait(TimeSpan.FromSeconds(5));
            _inner.Move(sourceFileName, destFileName, overwrite);
        }

        public void Delete(string path) => _inner.Delete(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern) => _inner.EnumerateFiles(path, searchPattern);
    }
}
