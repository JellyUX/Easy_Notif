using Jellyfin.Plugin.EasyNotif.Alerts;
using Jellyfin.Plugin.EasyNotif.Playback;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Scheduling;

/// <summary>
/// The plugin's single scheduled task. It ticks on startup and every 15 minutes, hands off to
/// <see cref="IDispatchService.RunDueAsync"/>, and returns immediately when nothing is due - there
/// is no continuous background loop (R13, Synthese.md section 10).
/// <para>
/// Auto-discovered by Jellyfin's scheduled-task subsystem via <c>ActivatorUtilities</c>; it is not
/// registered in DI (only <see cref="IDispatchService"/> is). This mirrors the sibling Homepage
/// plugin's tasks.
/// </para>
/// </summary>
public sealed class EasyNotifDispatchTask : IScheduledTask
{
    private static readonly long IntervalTicks = TimeSpan.FromMinutes(15).Ticks;

    private readonly IDispatchService _dispatch;
    private readonly ISeriesNotifier _series;
    private readonly IScheduledAnnouncementRunner _announcements;
    private readonly IAdminAlertService _alerts;
    private readonly IYearTotalSeeder _yearSeed;
    private readonly ILogger<EasyNotifDispatchTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="EasyNotifDispatchTask"/> class.</summary>
    /// <param name="dispatch">The dispatch service.</param>
    /// <param name="series">The series notifier, flushed after the campaigns.</param>
    /// <param name="announcements">The scheduled announcements, sent after the series.</param>
    /// <param name="alerts">The incident digest, sent last.</param>
    /// <param name="yearSeed">The one-time seed of the yearly total, run before the campaigns.</param>
    /// <param name="logger">Logger.</param>
    public EasyNotifDispatchTask(
        IDispatchService dispatch,
        ISeriesNotifier series,
        IScheduledAnnouncementRunner announcements,
        IAdminAlertService alerts,
        IYearTotalSeeder yearSeed,
        ILogger<EasyNotifDispatchTask> logger)
    {
        _dispatch = dispatch;
        _series = series;
        _announcements = announcements;
        _alerts = alerts;
        _yearSeed = yearSeed;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "Easy Notif - dispatch";

    /// <inheritdoc/>
    public string Key => "EasyNotifDispatch";

    /// <inheritdoc/>
    public string Description => "Evaluates the plugin's scheduled email campaigns and sends what is due.";

    /// <inheritdoc/>
    public string Category => "Easy Notif";

    /// <inheritdoc/>
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // Once, before the campaigns so a recap sent this tick already counts it (FEAT-08). The
        // library is loaded by the time a scheduled task runs. A failure is retried next tick.
        try
        {
            _yearSeed.RunOnce(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EasyNotif] Seeding the yearly total failed; it runs again on the next tick.");
        }

        await _dispatch.RunDueAsync(cancellationToken).ConfigureAwait(false);

        // After the campaigns, and isolated from them: a failing series flush is retried next tick.
        try
        {
            await _series.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[EasyNotif] Series notification flush failed; it runs again on the next tick.");
        }

        try
        {
            await _announcements.RunDueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[EasyNotif] Scheduled announcements failed; they run again on the next tick.");
        }

        // Last, so it reports what this tick ran into; a failure never affects the sends above.
        try
        {
            await _alerts.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[EasyNotif] Incident digest failed; it runs again on the next tick.");
        }

        progress.Report(100);
    }

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = IntervalTicks };
    }
}
