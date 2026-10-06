using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers the schema migration hook of <see cref="JsonFileStore{T}"/> (audit OPS-03): a current
/// file is read as is, an older one is migrated in memory and backed up before its first write, and
/// a newer one is read-only and never rewritten nor quarantined. Real <see cref="FileSystem"/>
/// against a per-test temp directory.
/// </summary>
public sealed class JsonFileStoreSchemaTests : IDisposable
{
    private const string FileName = "doc.json";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "enotif-schema-tests-" + Guid.NewGuid());

    private readonly List<IDisposable> _stores = [];

    private string DataDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif");

    private string FilePath => Path.Combine(DataDir, FileName);

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void CurrentSchemaFile_IsReadAsIs_WithoutBackup()
    {
        Seed("""{ "Schema": 1, "Name": "kept" }""");

        var store = Build(currentSchema: 1);
        store.SetName("changed");

        Assert.Equal("changed", store.Name);
        Assert.False(store.Migrated);
        Assert.Empty(Directory.GetFiles(DataDir, FileName + ".schema*.bak"));
    }

    [Fact]
    public void MissingSchemaProperty_IsReadAsSchemaOne()
    {
        Seed("""{ "Name": "legacy" }""");

        var store = Build(currentSchema: 2);

        Assert.True(store.Migrated);
        Assert.Equal(1, store.MigratedFrom);
    }

    [Fact]
    public void OlderSchema_IsMigratedInMemory_AndBackedUpOnFirstWriteOnly()
    {
        const string original = """{ "Schema": 1, "Name": "old" }""";
        Seed(original);

        var store = Build(currentSchema: 2);

        Assert.Equal("old (migrated)", store.Name);
        var backup = FilePath + ".schema1.bak";
        Assert.False(File.Exists(backup));

        store.SetName("new");

        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Contains("\"Schema\": 2", File.ReadAllText(FilePath), StringComparison.Ordinal);

        File.WriteAllText(backup, "first backup");
        store.SetName("newer");
        Assert.Equal("first backup", File.ReadAllText(backup));
    }

    [Fact]
    public void NewerSchema_IsReadOnly_AndNeverRewrittenNorQuarantined()
    {
        const string original = """{ "Schema": 99, "Name": "future", "Unknown": [1, 2] }""";
        Seed(original);

        var store = Build(currentSchema: 1);

        Assert.Equal("future", store.Name);
        var ex = Assert.Throws<StoreReadOnlyException>(() => store.SetName("lost"));
        Assert.IsAssignableFrom<IOException>(ex);
        Assert.Equal("future", store.Name);
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.Empty(Directory.GetFiles(DataDir, FileName + ".corrupt-*"));
    }

    [Fact]
    public void NewerSchema_WithAnIncompatibleShape_IsStillNotQuarantined()
    {
        const string original = """{ "Schema": 99, "Name": { "Nested": true } }""";
        Seed(original);

        var store = Build(currentSchema: 1);

        Assert.Null(store.Name);
        Assert.Throws<StoreReadOnlyException>(() => store.SetName("lost"));
        Assert.Equal(original, File.ReadAllText(FilePath));
        Assert.Empty(Directory.GetFiles(DataDir, FileName + ".corrupt-*"));
    }

    [Fact]
    public void NonIntegerSchema_TakesTheCorruptFilePath()
    {
        Seed("""{ "Schema": "two", "Name": "x" }""");

        var store = Build(currentSchema: 1);

        Assert.Null(store.Name);
        Assert.Single(Directory.GetFiles(DataDir, FileName + ".corrupt-*"));
    }

    [Fact]
    public void SendLog_JsonLinesWithoutSchema_StayWritable()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(
            Path.Combine(DataDir, "send-log.jsonl"),
            """{"Ts":"2026-10-06T10:00:00Z","Context":"test","ToMasked":"a***@b.co","Subject":"s","ResendId":"a","Status":"sent"}""" + "\n");
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);

        using var log = new SendLog(paths.Object, new FileSystem(), NullLogger<SendLog>.Instance);

        Assert.Empty(Directory.GetFiles(DataDir, "send-log.jsonl.corrupt-*"));
        Assert.True(log.UpdateStatus("a", "delivered"));
        Assert.Contains("delivered", File.ReadAllText(Path.Combine(DataDir, "send-log.jsonl")), StringComparison.Ordinal);
    }

    private void Seed(string json)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, json);
    }

    private TestStore Build(int currentSchema)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new TestStore(paths.Object, currentSchema);
        _stores.Add(store);
        return store;
    }

    private sealed class TestDoc
    {
        public int Schema { get; set; } = 1;

        public string? Name { get; set; }
    }

    private sealed class TestStore : JsonFileStore<TestDoc>
    {
        // Static: the base constructor reads CurrentSchema before this type's fields are assigned.
        [ThreadStatic]
        private static int _pendingSchema;

        private readonly int _currentSchema;

        public TestStore(IApplicationPaths paths, int currentSchema)
            : base(paths, new FileSystem(), NullLogger.Instance, Prime(currentSchema))
        {
            _currentSchema = currentSchema;
        }

        public bool Migrated { get; private set; }

        public int MigratedFrom { get; private set; }

        public string? Name => Read(d => d.Name);

        protected override int CurrentSchema => _currentSchema == 0 ? _pendingSchema : _currentSchema;

        public void SetName(string name) => Mutate(d =>
        {
            d.Name = name;
            return true;
        });

        protected override TestDoc Migrate(TestDoc document, int fromSchema)
        {
            Migrated = true;
            MigratedFrom = fromSchema;
            document.Name += " (migrated)";
            document.Schema = CurrentSchema;
            return document;
        }

        private static string Prime(int currentSchema)
        {
            _pendingSchema = currentSchema;
            return FileName;
        }
    }
}
