using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="AnnouncedStore"/>: a key is found again after a reload, only when announced on or
/// before the window start, only on its own channel, and is forgotten after the retention.
/// </summary>
public sealed class AnnouncedStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
    private const string Channel = AnnouncedStore.NewsletterChannel;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-announced-tests-" + Guid.NewGuid());
    private readonly List<AnnouncedStore> _stores = [];
    private DateTime _now = T0;

    private string FilePath => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "announced.json");

    private AnnouncedStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new AnnouncedStore(paths.Object, new FileSystem(), NullLogger<AnnouncedStore>.Instance, () => _now);
        _stores.Add(store);
        return store;
    }

    [Fact]
    public void Record_ThenReload_KeyIsFound_OnOrAfterItsRun()
    {
        Build().Record(Channel, ["ep:a:1:1", "movie:tmdb:1"], T0);

        var reloaded = Build();

        Assert.Equal(["ep:a:1:1", "movie:tmdb:1"], reloaded.AnnouncedOnOrBefore(Channel, ["ep:a:1:1", "movie:tmdb:1", "ep:a:1:2"], T0).Order());
        Assert.Empty(reloaded.AnnouncedOnOrBefore(Channel, ["ep:a:1:1"], T0.AddTicks(-1)));
    }

    [Fact]
    public void OtherChannel_DoesNotSeeTheKey()
    {
        var store = Build();
        store.Record(Channel, ["ep:a:1:1"], T0);

        Assert.Empty(store.AnnouncedOnOrBefore("series", ["ep:a:1:1"], T0));
    }

    [Fact]
    public void KeyOlderThanTheRetention_IsNotFound_AndIsDroppedOnTheNextWrite()
    {
        var store = Build();
        store.Record(Channel, ["old"], T0);

        _now = T0 + AnnouncedStore.Retention + TimeSpan.FromMinutes(1);
        Assert.Empty(store.AnnouncedOnOrBefore(Channel, ["old"], _now));

        store.Record(Channel, ["fresh"], _now);
        var raw = File.ReadAllText(FilePath);
        Assert.DoesNotContain("\"old\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"fresh\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingAKeyAgain_KeepsASingleEntry()
    {
        var store = Build();
        store.Record(Channel, ["k", "k"], T0);
        store.Record(Channel, ["k"], T0.AddDays(1));

        var raw = File.ReadAllText(FilePath);
        Assert.Equal(1, raw.Split("\"k\"").Length - 1);
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }
}
