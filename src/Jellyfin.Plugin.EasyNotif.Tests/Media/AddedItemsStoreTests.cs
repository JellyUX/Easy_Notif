using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Media;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="AddedItemsStore"/>: queued ids are drained and persisted with the server
/// timestamp, <see cref="AddedItemsStore.AddedSince"/> windows them, entries persist across
/// instances, and old entries are compacted out.
/// </summary>
public sealed class AddedItemsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-added-tests-" + Guid.NewGuid());
    private readonly List<AddedItemsStore> _stores = [];
    private DateTime _now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private AddedItemsStore Build(IFileSystem? fileSystem = null, ILogger<AddedItemsStore>? logger = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        // No real delay: the drain loop's debounce completes immediately.
        var store = new AddedItemsStore(
            paths.Object,
            fileSystem ?? new FileSystem(),
            logger ?? NullLogger<AddedItemsStore>.Instance,
            () => _now,
            (_, _) => Task.CompletedTask);
        _stores.Add(store);
        return store;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met in time");
    }

    [Fact]
    public async Task RecordAdded_IsDrainedAndPersistedWithTheServerTime()
    {
        var store = Build();
        store.Start();
        var id = Guid.NewGuid();

        store.RecordAdded(id);
        await WaitFor(() => store.AddedSince(_now.AddMinutes(-1)).Contains(id));

        Assert.Contains(id, store.AddedSince(_now.AddMinutes(-1)));
        Assert.Empty(store.AddedSince(_now.AddMinutes(1)));
        await store.StopAsync();
    }

    [Fact]
    public async Task Entries_PersistAcrossInstances()
    {
        var first = Build();
        first.Start();
        var id = Guid.NewGuid();
        first.RecordAdded(id);
        await WaitFor(() => first.AddedSince(_now.AddMinutes(-1)).Contains(id));
        await first.StopAsync();

        Assert.Contains(id, Build().AddedSince(_now.AddMinutes(-1)));
    }

    [Fact]
    public async Task OldEntries_AreCompactedOnTheNextWrite()
    {
        var store = Build();
        store.Start();

        var oldId = Guid.NewGuid();
        store.RecordAdded(oldId);
        await WaitFor(() => store.AddedSince(_now.AddDays(-1)).Contains(oldId));

        _now = _now.AddDays(120); // past the 90-day retention
        var freshId = Guid.NewGuid();
        store.RecordAdded(freshId);
        await WaitFor(() => store.AddedSince(_now.AddMinutes(-1)).Contains(freshId));

        Assert.DoesNotContain(oldId, store.AddedSince(DateTime.MinValue));
        await store.StopAsync();
    }

    [Fact]
    public async Task DrainLoop_SurvivesAWriteFailure_AndPersistsTheNextBatch()
    {
        var fs = new FailingFileSystem { ThrowOnNextWriteAllText = true };
        var logger = new Mock<ILogger<AddedItemsStore>>();
        var store = Build(fs, logger.Object);
        store.Start();

        // First batch: the flush throws. The id is lost, but the drain must keep running.
        store.RecordAdded(Guid.NewGuid());
        await WaitFor(() => fs.FailedWrites == 1);

        // Second batch: the drain is still alive and persists it.
        var freshId = Guid.NewGuid();
        store.RecordAdded(freshId);
        await WaitFor(() => store.AddedSince(_now.AddMinutes(-1)).Contains(freshId));

        logger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
        await store.StopAsync();
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

    /// <summary>A real <see cref="FileSystem"/> that can fail one write on demand.</summary>
    private sealed class FailingFileSystem : IFileSystem
    {
        private readonly FileSystem _inner = new();

        /// <summary>When true, the next <see cref="WriteAllText"/> throws once, then resets.</summary>
        public bool ThrowOnNextWriteAllText { get; set; }

        public int FailedWrites { get; private set; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents)
        {
            if (ThrowOnNextWriteAllText)
            {
                ThrowOnNextWriteAllText = false;
                FailedWrites++;
                throw new IOException("simulated disk failure");
            }

            _inner.WriteAllText(path, contents);
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite) => _inner.Move(sourceFileName, destFileName, overwrite);

        public void Delete(string path) => _inner.Delete(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern) => _inner.EnumerateFiles(path, searchPattern);
    }
}
