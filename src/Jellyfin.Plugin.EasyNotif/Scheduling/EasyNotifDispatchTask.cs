using Jellyfin.Plugin.EasyNotif.Playback;
using MediaBrowser.Model.Tasks;

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
    private readonly IPlaybackHistoryStore _history;

    /// <summary>Initializes a new instance of the <see cref="EasyNotifDispatchTask"/> class.</summary>
    /// <param name="dispatch">The dispatch service.</param>
    /// <param name="history">The playback history store (compacted here on the monthly rollover).</param>
    public EasyNotifDispatchTask(IDispatchService dispatch, IPlaybackHistoryStore history)
    {
        _dispatch = dispatch;
        _history = history;
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

        // Monthly event compaction: an in-memory no-op unless the calendar month has rolled over.
        _history.Compact();

        await _dispatch.RunDueAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc/>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = IntervalTicks };
    }
}
