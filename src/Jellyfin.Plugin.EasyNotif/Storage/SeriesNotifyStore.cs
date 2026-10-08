using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Persists the per-series notification state: who follows which series, the new episodes waiting
/// for their debounce window, and the keys removed recently (an upgrade must not notify). Inert
/// without a subscription: nothing is queued and the file is never written. See
/// <see cref="SeriesNotifyStore"/>.
/// </summary>
public interface ISeriesNotifyStore
{
    /// <summary>Gets a value indicating whether at least one user follows a series. In memory only.</summary>
    bool HasSubscriptions { get; }

    /// <summary>Gets a value indicating whether at least one episode is pending. In memory only.</summary>
    bool HasPending { get; }

    /// <summary>Returns whether at least one user follows the series. In memory only.</summary>
    /// <param name="seriesId">The series id.</param>
    /// <returns>True when the series is followed.</returns>
    bool IsFollowed(Guid seriesId);

    /// <summary>Returns detached copies of the subscriptions.</summary>
    /// <returns>The subscriptions.</returns>
    IReadOnlyList<SeriesSubscription> Subscriptions();

    /// <summary>Adds a subscription, if not already present.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="seriesId">The series id.</param>
    /// <param name="source">How the subscription was made (<c>bell</c>, <c>panel</c>).</param>
    /// <param name="nowUtc">The current time (UTC).</param>
    void Subscribe(Guid userId, Guid seriesId, string source, DateTime nowUtc);

    /// <summary>Removes a subscription, if present.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="seriesId">The series id.</param>
    void Unsubscribe(Guid userId, Guid seriesId);

    /// <summary>Returns detached copies of the pending episodes.</summary>
    /// <returns>The pending episodes.</returns>
    IReadOnlyList<PendingEpisode> Pending();

    /// <summary>Queues new episodes of followed series; an item already pending or of an unfollowed series is ignored.</summary>
    /// <param name="episodes">The item and series ids.</param>
    /// <param name="nowUtc">The time the episodes were seen (UTC).</param>
    /// <returns>The number of episodes queued.</returns>
    int Enqueue(IEnumerable<(Guid ItemId, Guid SeriesId)> episodes, DateTime nowUtc);

    /// <summary>Drops a removed item from the queue and remembers its key for <see cref="SeriesNotifyStore.RemovedRetention"/>.</summary>
    /// <param name="itemId">The removed item id.</param>
    /// <param name="key">The item's media key, or null when it could not be computed.</param>
    /// <param name="nowUtc">The current time (UTC).</param>
    void RecordRemoved(Guid itemId, string? key, DateTime nowUtc);

    /// <summary>Returns the keys removed within <see cref="SeriesNotifyStore.RemovedRetention"/>.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>The removed keys.</returns>
    IReadOnlySet<string> RecentlyRemoved(DateTime nowUtc);

    /// <summary>Counts one more metadata deferral for each of the items.</summary>
    /// <param name="itemIds">The pending item ids.</param>
    void Defer(IEnumerable<Guid> itemIds);

    /// <summary>Removes the items from the queue.</summary>
    /// <param name="itemIds">The pending item ids.</param>
    void Drop(IEnumerable<Guid> itemIds);

    /// <summary>
    /// Records that the user was served the items, then removes every entry all current followers of
    /// its series were served.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="itemIds">The pending item ids.</param>
    void MarkNotified(Guid userId, IEnumerable<Guid> itemIds);

    /// <summary>
    /// Drops pending entries older than <see cref="SeriesNotifyStore.PendingRetention"/>, removed keys
    /// older than <see cref="SeriesNotifyStore.RemovedRetention"/>, and entries whose series no longer
    /// has a follower.
    /// </summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>The number of pending entries dropped.</returns>
    int Prune(DateTime nowUtc);

    /// <summary>Records one series email sent to the user, and forgets those older than a day.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="nowUtc">The current time (UTC).</param>
    void RecordSend(Guid userId, DateTime nowUtc);

    /// <summary>Counts the series emails sent to the user since a time (at most a day back).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="sinceUtc">The start of the count (UTC).</param>
    /// <returns>The number of series emails.</returns>
    int SentSince(Guid userId, DateTime sinceUtc);
}

/// <summary>On-disk shape of <c>series-notify.json</c>.</summary>
public sealed class SeriesNotifyFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the subscriptions.</summary>
    public List<SeriesSubscription> Subscriptions { get; init; } = [];

    /// <summary>Gets the series a user opted out of (reserved for automatic subscriptions).</summary>
    public List<SeriesExclusion> Exclusions { get; init; } = [];

    /// <summary>Gets the episodes waiting for their debounce window.</summary>
    public List<PendingEpisode> Pending { get; init; } = [];

    /// <summary>Gets the keys removed recently.</summary>
    public List<RemovedKey> RemovedRecent { get; init; } = [];

    /// <summary>Gets the series emails sent over the last day, for the per-user daily cap.</summary>
    public List<SeriesSend> Sends { get; init; } = [];
}

/// <summary>One user following one series.</summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="SeriesId">The series id.</param>
/// <param name="Since">The UTC time of the subscription.</param>
/// <param name="Source">How the subscription was made.</param>
public sealed record SeriesSubscription(Guid UserId, Guid SeriesId, DateTime Since, string Source);

/// <summary>One series a user opted out of.</summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="SeriesId">The series id.</param>
public sealed record SeriesExclusion(Guid UserId, Guid SeriesId);

/// <summary>One removed media key.</summary>
/// <param name="Key">The media key.</param>
/// <param name="At">The UTC time of the removal.</param>
public sealed record RemovedKey(string Key, DateTime At);

/// <summary>One series email sent to a user.</summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="At">When it was sent (UTC).</param>
public sealed record SeriesSend(Guid UserId, DateTime At);

/// <summary>One new episode waiting for its debounce window. Ids only, no title.</summary>
public sealed class PendingEpisode
{
    /// <summary>Gets or sets the episode item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the series id.</summary>
    public Guid SeriesId { get; set; }

    /// <summary>Gets or sets the UTC time the episode was seen.</summary>
    public DateTime FirstSeen { get; set; }

    /// <summary>Gets or sets the number of flushes deferred for missing metadata.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the users already served this episode.</summary>
    public List<Guid> Notified { get; set; } = [];

    /// <summary>Returns a detached copy.</summary>
    /// <returns>The copy.</returns>
    public PendingEpisode Clone()
    {
        var copy = (PendingEpisode)MemberwiseClone();
        copy.Notified = [.. Notified];
        return copy;
    }
}

/// <inheritdoc cref="ISeriesNotifyStore"/>
public sealed class SeriesNotifyStore : JsonFileStore<SeriesNotifyFile>, ISeriesNotifyStore
{
    /// <summary>How long a pending episode is kept.</summary>
    public static readonly TimeSpan PendingRetention = TimeSpan.FromDays(7);

    /// <summary>How long a removed key blocks a notification.</summary>
    public static readonly TimeSpan RemovedRetention = TimeSpan.FromHours(24);

    /// <summary>How long a sent series email is remembered for the per-user daily cap.</summary>
    public static readonly TimeSpan SendRetention = TimeSpan.FromHours(24);

    private volatile HashSet<Guid> _followed;
    private volatile bool _hasPending;

    /// <summary>Initializes a new instance of the <see cref="SeriesNotifyStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public SeriesNotifyStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<SeriesNotifyStore> logger)
        : base(applicationPaths, fileSystem, logger, "series-notify.json")
    {
        _followed = Read(FollowedOf);
        _hasPending = Read(file => file.Pending.Count > 0);
    }

    /// <inheritdoc/>
    public bool HasSubscriptions => _followed.Count > 0;

    /// <inheritdoc/>
    public bool HasPending => _hasPending;

    /// <inheritdoc/>
    public bool IsFollowed(Guid seriesId) => _followed.Contains(seriesId);

    /// <inheritdoc/>
    public IReadOnlyList<SeriesSubscription> Subscriptions()
        => Read(file => (IReadOnlyList<SeriesSubscription>)[.. file.Subscriptions]);

    /// <inheritdoc/>
    public void Subscribe(Guid userId, Guid seriesId, string source, DateTime nowUtc)
        => MutateTracked(file =>
        {
            if (file.Subscriptions.Any(s => s.UserId == userId && s.SeriesId == seriesId))
            {
                return false;
            }

            file.Subscriptions.Add(new SeriesSubscription(userId, seriesId, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), source));
            return true;
        });

    /// <inheritdoc/>
    public void Unsubscribe(Guid userId, Guid seriesId)
        => MutateTracked(file => file.Subscriptions.RemoveAll(s => s.UserId == userId && s.SeriesId == seriesId) > 0);

    /// <inheritdoc/>
    public IReadOnlyList<PendingEpisode> Pending()
        => Read(file => (IReadOnlyList<PendingEpisode>)[.. file.Pending.Select(p => p.Clone())]);

    /// <inheritdoc/>
    public int Enqueue(IEnumerable<(Guid ItemId, Guid SeriesId)> episodes, DateTime nowUtc)
    {
        var batch = episodes.Where(e => IsFollowed(e.SeriesId)).ToList();
        if (batch.Count == 0)
        {
            return 0;
        }

        var added = 0;
        var seen = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        MutateTracked(file =>
        {
            var known = file.Pending.Select(p => p.ItemId).ToHashSet();
            foreach (var (itemId, seriesId) in batch.Where(e => known.Add(e.ItemId)))
            {
                file.Pending.Add(new PendingEpisode { ItemId = itemId, SeriesId = seriesId, FirstSeen = seen });
                added++;
            }

            return added > 0;
        });
        return added;
    }

    /// <inheritdoc/>
    public void RecordRemoved(Guid itemId, string? key, DateTime nowUtc)
    {
        var at = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        MutateTracked(file =>
        {
            var changed = file.Pending.RemoveAll(p => p.ItemId == itemId) > 0;
            changed |= file.RemovedRecent.RemoveAll(r => at - r.At >= RemovedRetention) > 0;
            if (key is not null)
            {
                file.RemovedRecent.RemoveAll(r => r.Key == key);
                file.RemovedRecent.Add(new RemovedKey(key, at));
                changed = true;
            }

            return changed;
        });
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> RecentlyRemoved(DateTime nowUtc)
        => Read(file => (IReadOnlySet<string>)file.RemovedRecent
            .Where(r => nowUtc - r.At < RemovedRetention)
            .Select(r => r.Key)
            .ToHashSet(StringComparer.Ordinal));

    /// <inheritdoc/>
    public void Defer(IEnumerable<Guid> itemIds)
    {
        var ids = itemIds.ToHashSet();
        MutateTracked(file =>
        {
            var changed = false;
            foreach (var entry in file.Pending.Where(p => ids.Contains(p.ItemId)))
            {
                entry.Attempts++;
                changed = true;
            }

            return changed;
        });
    }

    /// <inheritdoc/>
    public void Drop(IEnumerable<Guid> itemIds)
    {
        var ids = itemIds.ToHashSet();
        if (ids.Count > 0)
        {
            MutateTracked(file => file.Pending.RemoveAll(p => ids.Contains(p.ItemId)) > 0);
        }
    }

    /// <inheritdoc/>
    public void MarkNotified(Guid userId, IEnumerable<Guid> itemIds)
    {
        var ids = itemIds.ToHashSet();
        if (ids.Count == 0)
        {
            return;
        }

        MutateTracked(file =>
        {
            var changed = false;
            foreach (var entry in file.Pending.Where(p => ids.Contains(p.ItemId) && !p.Notified.Contains(userId)))
            {
                entry.Notified.Add(userId);
                changed = true;
            }

            return RemoveServed(file) | changed;
        });
    }

    /// <inheritdoc/>
    public int Prune(DateTime nowUtc)
    {
        var dropped = 0;
        MutateTracked(file =>
        {
            var before = file.Pending.Count;
            file.Pending.RemoveAll(p => nowUtc - p.FirstSeen >= PendingRetention);
            RemoveServed(file);
            dropped = before - file.Pending.Count;
            var removed = file.RemovedRecent.RemoveAll(r => nowUtc - r.At >= RemovedRetention);
            return dropped > 0 || removed > 0;
        });
        return dropped;
    }

    /// <inheritdoc/>
    public void RecordSend(Guid userId, DateTime nowUtc)
    {
        Mutate(file =>
        {
            file.Sends.RemoveAll(s => nowUtc - s.At >= SendRetention);
            file.Sends.Add(new SeriesSend(userId, nowUtc));
            return true;
        });
    }

    /// <inheritdoc/>
    public int SentSince(Guid userId, DateTime sinceUtc) =>
        Read(file => file.Sends.Count(s => s.UserId == userId && s.At >= sinceUtc));

    // An entry is done once every current follower of its series was served (a follower who left
    // during the window no longer counts); a series nobody follows any more empties the same way.
    private static bool RemoveServed(SeriesNotifyFile file)
    {
        var followers = file.Subscriptions
            .GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.UserId).ToList());
        return file.Pending.RemoveAll(p =>
            !followers.TryGetValue(p.SeriesId, out var users) || users.All(p.Notified.Contains)) > 0;
    }

    private static HashSet<Guid> FollowedOf(SeriesNotifyFile file) => file.Subscriptions.Select(s => s.SeriesId).ToHashSet();

    private void MutateTracked(Func<SeriesNotifyFile, bool> mutation)
    {
        try
        {
            Mutate(mutation);
        }
        finally
        {
            _followed = Read(FollowedOf);
            _hasPending = Read(file => file.Pending.Count > 0);
        }
    }
}
