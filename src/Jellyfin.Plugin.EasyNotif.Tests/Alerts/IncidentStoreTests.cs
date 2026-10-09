using Jellyfin.Plugin.EasyNotif.Alerts;
using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Alerts;

/// <summary>
/// Covers <see cref="IncidentStore"/>: only incident event types are kept, with a short detail and
/// never an address field; old incidents expire; a suppressed scope records nothing; the digest
/// time persists across instances.
/// </summary>
public sealed class IncidentStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-incidents-tests-" + Guid.NewGuid());
    private readonly List<IncidentStore> _stores = [];
    private DateTime _now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    private IncidentStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        var store = new IncidentStore(paths.Object, new FileSystem(), NullLogger<IncidentStore>.Instance, () => _now);
        _stores.Add(store);
        return store;
    }

    [Fact]
    public void Record_KeepsIncidentTypesOnly_WithAShortDetail()
    {
        var store = Build();

        store.Record("email.sent", new Dictionary<string, object?> { ["subject"] = "Hello" });
        store.Record("access.error", null);
        store.Record("dispatch.error", new Dictionary<string, object?> { ["campaignId"] = "newsletter", ["to"] = "a***e@example.org" });

        var incident = Assert.Single(store.All());
        Assert.Equal("dispatch.error", incident.Type);
        Assert.Equal(_now, incident.Ts);
        Assert.Equal("campaignId=newsletter", incident.Detail);
    }

    [Fact]
    public void Detail_IsTruncated()
    {
        var detail = IncidentStore.Detail(new Dictionary<string, object?> { ["error"] = new string('x', 500) });

        Assert.Equal(200, detail!.Length);
    }

    [Fact]
    public void All_DropsIncidentsOlderThanSevenDays()
    {
        var store = Build();
        store.Record("drain.lost", null);

        _now = _now.AddDays(7).AddMinutes(1);
        store.Record("quota.threshold", null);

        Assert.Equal("quota.threshold", Assert.Single(store.All()).Type);
    }

    [Fact]
    public async Task Suppress_IgnoresRecordsOnTheSameFlow_UntilDisposed()
    {
        var store = Build();

        using (store.Suppress())
        {
            await Task.Yield();
            store.Record("email.failed", null);
        }

        Assert.Empty(store.All());
        store.Record("email.failed", null);
        Assert.Single(store.All());
    }

    [Fact]
    public void MarkDigestSent_PersistsAcrossInstances()
    {
        Build().MarkDigestSent(_now);

        Assert.Equal(_now, Build().LastDigestUtc);
    }

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
