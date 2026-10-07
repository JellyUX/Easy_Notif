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
    private readonly IPlaybackHistoryStore _history;
    private readonly ILogger<EasyNotifDispatchTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="EasyNotifDispatchTask"/> class.</summary>
    /// <param name="dispatch">The dispatch service.</param>
    /// <param name="history">The playback history store (compacted here on the monthly rollover).</param>
    /// <param name="series">The series notifier, flushed after the campaigns.</param>
    /// <param name="logger">Logger.</param>
    public EasyNotifDispatchTask(IDispatchService dispatch, IPlaybackHistoryStore history, ISeriesNotifier series, ILogger<EasyNotifDispatchTask> logger)
    {
        _dispatch = dispatch;
        _series = series;
        _history = history;
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

        // Monthly event compaction: an in-memory no-op unless the calendar month has rolled over. A
        // failure here must not keep the campaigns from being dispatched.
        try
        {
            _history.Compact();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[EasyNotif] Playback history compaction failed; dispatching anyway.");
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

        progress.Report(100);
    }

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = IntervalTicks };
    }
}
