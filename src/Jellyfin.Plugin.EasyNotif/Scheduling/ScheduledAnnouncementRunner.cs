using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Storage;

namespace Jellyfin.Plugin.EasyNotif.Scheduling;

/// <summary>
/// Sends the scheduled announcements that are due, from the dispatch tick (FEAT-05). Each one goes
/// out as a manual lot whose id is the announcement id, so every recipient's key is
/// <c>manual:{id}:{user}</c> and no pass, retry or replay after a crash delivers twice. Transient
/// failures are retried on the campaign backoff, then abandoned with an incident (DEC-71).
/// </summary>
public interface IScheduledAnnouncementRunner
{
    /// <summary>Sends what is due and purges the old finished announcements.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task RunDueAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IScheduledAnnouncementRunner"/>
public sealed class ScheduledAnnouncementRunner : IScheduledAnnouncementRunner
{
    private readonly IScheduledAnnouncementStore _store;
    private readonly IManualEmailService _manual;
    private readonly IQuotaGuard _quota;
    private readonly IConfigAccessor _config;
    private readonly ISecretStore _secrets;
    private readonly IEasyNotifLog _log;
    private readonly Func<DateTime> _now;

    /// <summary>Initializes a new instance of the <see cref="ScheduledAnnouncementRunner"/> class.</summary>
    /// <param name="store">The scheduled announcement store.</param>
    /// <param name="manual">The manual email service that sends each announcement.</param>
    /// <param name="quota">The send quota guard.</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="secrets">The transport secret store.</param>
    /// <param name="log">The plugin event log.</param>
    public ScheduledAnnouncementRunner(
        IScheduledAnnouncementStore store,
        IManualEmailService manual,
        IQuotaGuard quota,
        IConfigAccessor config,
        ISecretStore secrets,
        IEasyNotifLog log)
        : this(store, manual, quota, config, secrets, log, () => DateTime.UtcNow)
    {
    }

    internal ScheduledAnnouncementRunner(
        IScheduledAnnouncementStore store,
        IManualEmailService manual,
        IQuotaGuard quota,
        IConfigAccessor config,
        ISecretStore secrets,
        IEasyNotifLog log,
        Func<DateTime> now)
    {
        _store = store;
        _manual = manual;
        _quota = quota;
        _config = config;
        _secrets = secrets;
        _log = log;
        _now = now;
    }

    /// <inheritdoc/>
    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        var now = _now();
        _store.Purge(now);

        var due = _store.Due(now);
        if (due.Count == 0)
        {
            return;
        }

        // Not sendable right now: everything stays due and the next tick tries again.
        var reason = string.IsNullOrWhiteSpace(_secrets.Get().ResendApiKey) || string.IsNullOrWhiteSpace(_config.Get().FromEmail)
            ? "transport-not-configured"
            : _quota.Snapshot().Over ? "quota-over" : null;
        if (reason is not null)
        {
            _log.Warn("announcement.skipped", new Dictionary<string, object?> { ["count"] = due.Count, ["reason"] = reason });
            return;
        }

        foreach (var announcement in due)
        {
            await SendAsync(announcement, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(ScheduledAnnouncement announcement, DateTime now, CancellationToken cancellationToken)
    {
        var retrying = announcement.Status == ScheduledAnnouncement.Retrying;

        ManualEmailPlan plan;
        try
        {
            plan = _manual.Plan(announcement.ToRequest());
        }
        catch (ArgumentException ex)
        {
            announcement.Status = ScheduledAnnouncement.Failed;
            announcement.FinishedUtc = now;
            _store.Update(announcement);
            _log.Warn("announcement.failed", new Dictionary<string, object?> { ["subject"] = announcement.Subject, ["reason"] = ex.Message });
            return;
        }

        var result = await _manual.SendAsync(
            plan,
            announcement.Id,
            retrying ? announcement.RetryUserIds.ToHashSet() : null,
            null,
            cancellationToken).ConfigureAwait(false);

        var transient = result.Details.Where(d => d.Status == "failed" && d.Transient).Select(d => d.UserId).ToList();
        announcement.Attempts++;
        announcement.SentCount += result.Sent;
        announcement.FailedCount += result.Failed - transient.Count;
        if (!retrying)
        {
            announcement.SkippedNoEmail = result.SkippedNoEmail;
        }

        if (transient.Count == 0)
        {
            Finish(announcement, now);
        }
        else if (announcement.Attempts > DispatchService.RetryBackoff.Length || now - announcement.SendAtUtc >= DispatchService.RetryWindow)
        {
            _log.Warn("announcement.retry-abandoned", new Dictionary<string, object?>
            {
                ["subject"] = announcement.Subject,
                ["attempts"] = announcement.Attempts,
                ["count"] = transient.Count
            });
            announcement.FailedCount += transient.Count;
            Finish(announcement, now);
        }
        else
        {
            announcement.Status = ScheduledAnnouncement.Retrying;
            announcement.RetryUserIds = transient;
            announcement.NextAttemptUtc = now + DispatchService.RetryBackoff[announcement.Attempts - 1];
        }

        _store.Update(announcement);
        _log.Info("announcement.sent", new Dictionary<string, object?>
        {
            ["subject"] = announcement.Subject,
            ["attempt"] = announcement.Attempts,
            ["sent"] = result.Sent,
            ["failed"] = result.Failed,
            ["retrying"] = transient.Count
        });
    }

    private static void Finish(ScheduledAnnouncement announcement, DateTime now)
    {
        announcement.Status = ScheduledAnnouncement.Sent;
        announcement.RetryUserIds = [];
        announcement.NextAttemptUtc = null;
        announcement.FinishedUtc = now;
    }
}
