using Jellyfin.Plugin.EasyNotif.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// In-memory health counters of one background drain (OPS-02, DEC-62), kept since the server
/// started: a drain that can no longer write could not persist its own failure. A lost batch is
/// logged as <c>drain.lost</c> (Error); items refused by a full channel are counted at once and
/// reported as one <c>drain.dropped</c> (Warn) line at the next flush attempt, not one per item.
/// </summary>
public sealed class DrainHealth
{
    private readonly string _name;
    private readonly IEasyNotifLog _log;
    private readonly Func<int> _depth;
    private readonly object _gate = new();
    private DateTime? _lastFlushUtc;
    private long _lostBatches;
    private long _lostItems;
    private long _dropped;
    private long _droppedReported;

    /// <summary>Initializes a new instance of the <see cref="DrainHealth"/> class.</summary>
    /// <param name="name">The drain name used in logs and the status (for example <c>"newItems"</c>).</param>
    /// <param name="log">The plugin log.</param>
    /// <param name="depth">Reads the number of items waiting in the channel.</param>
    public DrainHealth(string name, IEasyNotifLog log, Func<int> depth)
    {
        _name = name;
        _log = log;
        _depth = depth;
    }

    /// <summary>Counts one item refused by a full channel. Non-blocking, never logs.</summary>
    public void Dropped() => Interlocked.Increment(ref _dropped);

    /// <summary>Records a batch written to disk.</summary>
    /// <param name="nowUtc">The flush time (UTC).</param>
    public void Flushed(DateTime nowUtc)
    {
        lock (_gate)
        {
            _lastFlushUtc = nowUtc;
        }

        ReportDropped();
    }

    /// <summary>Records a batch lost to a write failure and logs it as <c>drain.lost</c>.</summary>
    /// <param name="count">How many items the batch held.</param>
    /// <param name="exception">The write failure.</param>
    public void Lost(int count, Exception exception)
    {
        Interlocked.Increment(ref _lostBatches);
        Interlocked.Add(ref _lostItems, count);
        _log.Error("drain.lost", new Dictionary<string, object?> { ["drain"] = _name, ["count"] = count }, exception);
        ReportDropped();
    }

    /// <summary>Returns the current counters.</summary>
    /// <returns>An immutable snapshot.</returns>
    public DrainHealthSnapshot Snapshot()
    {
        DateTime? lastFlush;
        lock (_gate)
        {
            lastFlush = _lastFlushUtc;
        }

        return new DrainHealthSnapshot(
            lastFlush,
            Interlocked.Read(ref _lostBatches),
            Interlocked.Read(ref _lostItems),
            Interlocked.Read(ref _dropped),
            _depth());
    }

    private void ReportDropped()
    {
        var dropped = Interlocked.Read(ref _dropped);
        var reported = Interlocked.Exchange(ref _droppedReported, dropped);
        if (dropped > reported)
        {
            _log.Warn("drain.dropped", new Dictionary<string, object?> { ["drain"] = _name, ["count"] = dropped - reported });
        }
    }
}

/// <summary>A point-in-time view of a <see cref="DrainHealth"/>.</summary>
/// <param name="LastFlushUtc">The last successful write (UTC), or null since the server started.</param>
/// <param name="LostBatches">Batches lost to a write failure.</param>
/// <param name="LostItems">Items in those batches.</param>
/// <param name="Dropped">Items refused because the channel was full.</param>
/// <param name="Depth">Items waiting in the channel now.</param>
public sealed record DrainHealthSnapshot(DateTime? LastFlushUtc, long LostBatches, long LostItems, long Dropped, int Depth);
