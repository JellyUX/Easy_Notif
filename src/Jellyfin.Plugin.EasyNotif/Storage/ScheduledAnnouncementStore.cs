using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Persists the scheduled announcements in <c>scheduled-announcements.json</c>, a file of its own so
/// that a downgrade to a version without them leaves <c>campaigns.json</c> untouched (DEC-66).
/// </summary>
public interface IScheduledAnnouncementStore
{
    /// <summary>Returns every announcement, soonest first.</summary>
    /// <returns>Detached copies.</returns>
    IReadOnlyList<ScheduledAnnouncement> All();

    /// <summary>Returns the announcements to send now: pending and due, or retrying and due.</summary>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <returns>Detached copies.</returns>
    IReadOnlyList<ScheduledAnnouncement> Due(DateTime nowUtc);

    /// <summary>Adds an announcement.</summary>
    /// <param name="announcement">The announcement.</param>
    void Add(ScheduledAnnouncement announcement);

    /// <summary>Replaces a stored announcement by id.</summary>
    /// <param name="announcement">The new state.</param>
    void Update(ScheduledAnnouncement announcement);

    /// <summary>Cancels a pending announcement.</summary>
    /// <param name="id">The id.</param>
    /// <param name="nowUtc">The current UTC time.</param>
    /// <returns>The outcome.</returns>
    CancelOutcome Cancel(Guid id, DateTime nowUtc);

    /// <summary>Removes the finished announcements older than <see cref="ScheduledAnnouncementStore.FinishedRetention"/>.</summary>
    /// <param name="nowUtc">The current UTC time.</param>
    void Purge(DateTime nowUtc);
}

/// <summary>The outcome of <see cref="IScheduledAnnouncementStore.Cancel"/>.</summary>
public enum CancelOutcome
{
    /// <summary>The announcement was pending and is now cancelled.</summary>
    Cancelled,

    /// <summary>No announcement has this id.</summary>
    NotFound,

    /// <summary>The announcement was already sent, retrying, cancelled or failed.</summary>
    NotPending
}

/// <inheritdoc cref="IScheduledAnnouncementStore"/>
public sealed class ScheduledAnnouncementStore : JsonFileStore<ScheduledAnnouncementFile>, IScheduledAnnouncementStore
{
    /// <summary>How long a finished announcement stays listed with its counters (DEC-72).</summary>
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(30);

    /// <summary>Initializes a new instance of the <see cref="ScheduledAnnouncementStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public ScheduledAnnouncementStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<ScheduledAnnouncementStore> logger)
        : base(applicationPaths, fileSystem, logger, "scheduled-announcements.json")
    {
    }

    /// <inheritdoc/>
    public IReadOnlyList<ScheduledAnnouncement> All()
        => Read(file => (IReadOnlyList<ScheduledAnnouncement>)[.. file.Announcements.OrderBy(a => a.SendAtUtc).Select(a => a.Clone())]);

    /// <inheritdoc/>
    public IReadOnlyList<ScheduledAnnouncement> Due(DateTime nowUtc)
        => Read(file => (IReadOnlyList<ScheduledAnnouncement>)
        [
            .. file.Announcements
                .Where(a => (a.Status == ScheduledAnnouncement.Pending && a.SendAtUtc <= nowUtc)
                    || (a.Status == ScheduledAnnouncement.Retrying && a.NextAttemptUtc <= nowUtc))
                .OrderBy(a => a.SendAtUtc)
                .Select(a => a.Clone())
        ]);

    /// <inheritdoc/>
    public void Add(ScheduledAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var copy = announcement.Clone();
        Mutate(file =>
        {
            file.Announcements.Add(copy);
            return true;
        });
    }

    /// <inheritdoc/>
    public void Update(ScheduledAnnouncement announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var copy = announcement.Clone();
        Mutate(file =>
        {
            var index = file.Announcements.FindIndex(a => a.Id == copy.Id);
            if (index < 0)
            {
                return false;
            }

            file.Announcements[index] = copy;
            return true;
        });
    }

    /// <inheritdoc/>
    public CancelOutcome Cancel(Guid id, DateTime nowUtc)
    {
        var outcome = CancelOutcome.NotFound;
        Mutate(file =>
        {
            var announcement = file.Announcements.FirstOrDefault(a => a.Id == id);
            if (announcement is null)
            {
                return false;
            }

            if (announcement.Status != ScheduledAnnouncement.Pending)
            {
                outcome = CancelOutcome.NotPending;
                return false;
            }

            announcement.Status = ScheduledAnnouncement.Cancelled;
            announcement.FinishedUtc = nowUtc;
            outcome = CancelOutcome.Cancelled;
            return true;
        });
        return outcome;
    }

    /// <inheritdoc/>
    public void Purge(DateTime nowUtc)
        => Mutate(file => file.Announcements.RemoveAll(a => a.IsFinished && a.FinishedUtc is { } done && nowUtc - done > FinishedRetention) > 0);
}
