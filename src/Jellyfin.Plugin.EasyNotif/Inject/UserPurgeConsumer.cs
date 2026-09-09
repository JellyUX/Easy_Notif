using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Playback;
using Jellyfin.Plugin.EasyNotif.Services;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.EasyNotif.Inject;

/// <summary>
/// Erases a user's stored data the moment their Jellyfin account is deleted: the contact address and
/// opt-ins in <c>preferences.json</c>, and the playback events and rollup in
/// <c>playback-history.json</c>. Without this, an orphaned row keeps a cleartext address on disk and
/// the scheduler keeps mailing the deleted account (a GDPR erasure failure).
/// </summary>
public sealed class UserPurgeConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly IPreferenceService _preferences;
    private readonly IPlaybackHistoryStore _history;
    private readonly IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="UserPurgeConsumer"/> class.</summary>
    /// <param name="preferences">The preference service.</param>
    /// <param name="history">The playback history store.</param>
    /// <param name="log">The plugin log.</param>
    public UserPurgeConsumer(IPreferenceService preferences, IPlaybackHistoryStore history, IEasyNotifLog log)
    {
        _preferences = preferences;
        _history = history;
        _log = log;
    }

    /// <inheritdoc/>
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        var userId = eventArgs.Argument.Id;
        try
        {
            _preferences.Purge(userId);
            _history.Purge(userId);
            _log.Info("user.purged", new Dictionary<string, object?> { ["userId"] = userId });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _log.Error("user.purge.failed", new Dictionary<string, object?> { ["userId"] = userId }, ex);
        }

        return Task.CompletedTask;
    }
}
