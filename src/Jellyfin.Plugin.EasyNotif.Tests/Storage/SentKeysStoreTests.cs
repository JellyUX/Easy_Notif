using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="SentKeysStore"/>: a recorded key is found again after a reload, a key older than
/// the retention is neither found nor kept on the next write.
/// </summary>
public sealed class SentKeysStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-sentkeys-tests-" + Guid.NewGuid());
    private readonly List<SentKeysStore> _stores = [];
    private DateTime _now = T0;

    private string FilePath => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "sent-keys.json");

    private SentKeysStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new SentKeysStore(paths.Object, new FileSystem(), NullLogger<SentKeysStore>.Instance, () => _now);
        _stores.Add(store);
        return store;
    }

    [Fact]
    public void Record_ThenReload_KeyIsFound()
    {
        Build().Record("newsletter:202606150900:u1");

        var reloaded = Build();

        Assert.True(reloaded.Contains("newsletter:202606150900:u1"));
        Assert.False(reloaded.Contains("newsletter:202606150900:u2"));
    }

    [Fact]
    public void KeyOlderThanTheRetention_IsNotFound_AndIsDroppedOnTheNextWrite()
    {
        var store = Build();
        store.Record("old");

        _now = T0 + SentKeysStore.Retention + TimeSpan.FromMinutes(1);
        Assert.False(store.Contains("old"));

        store.Record("new");
        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("\"old\"", text, StringComparison.Ordinal);
        Assert.Contains("\"new\"", text, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
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
}
