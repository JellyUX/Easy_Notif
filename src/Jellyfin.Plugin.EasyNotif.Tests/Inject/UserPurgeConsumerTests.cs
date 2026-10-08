using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.EasyNotif.Inject;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Inject;

/// <summary>
/// Covers <see cref="UserPurgeConsumer"/>: a delete event purges the three stores, and a storage error
/// is swallowed and logged rather than propagated back into Jellyfin's event bus.
/// </summary>
public sealed class UserPurgeConsumerTests
{
    private readonly Mock<IPreferenceService> _preferences = new();
    private readonly FakePlaybackHistoryStore _history = new();
    private readonly Mock<ISeriesNotifyStore> _series = new();
    private readonly Mock<IEasyNotifLog> _log = new();

    private UserPurgeConsumer Build() => new(_preferences.Object, _history, _series.Object, _log.Object);

    private static UserDeletedEventArgs Deleted(Guid id)
        => new(new User("gone", "Default", "Default") { Id = id });

    [Fact]
    public async Task OnEvent_PurgesEveryStore_AndLogs()
    {
        var id = Guid.NewGuid();

        await Build().OnEvent(Deleted(id));

        _preferences.Verify(p => p.Purge(id), Times.Once);
        Assert.Contains(id, _history.Purged);
        _series.Verify(s => s.PurgeUser(id), Times.Once);
        _log.Verify(l => l.Info("user.purged", It.IsAny<IReadOnlyDictionary<string, object?>>()), Times.Once);
    }

    [Fact]
    public async Task OnEvent_SwallowsAStorageError_AndLogsIt()
    {
        var id = Guid.NewGuid();
        _preferences.Setup(p => p.Purge(id)).Throws(new IOException("disk gone"));

        await Build().OnEvent(Deleted(id));

        _log.Verify(
            l => l.Error("user.purge.failed", It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<IOException>()),
            Times.Once);
    }
}
