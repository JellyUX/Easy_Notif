using System.Globalization;
using System.Threading.Channels;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Playback;

/// <summary>
/// The plugin's own lightweight watch history: an append-only event list plus a per-user/per-year
/// rollup, in one JSON file (Synthese.md section 6). The rollup is kept forever (a few KB); the
/// events are compacted to 90 days. See <see cref="PlaybackHistoryStore"/>.
/// </summary>
public interface IPlaybackHistoryStore
{
    /// <summary>Queues one playback event to be recorded. Non-blocking (R13).</summary>
    /// <param name="playbackEvent">The event.</param>
    void Record(PlaybackEvent playbackEvent);

    /// <summary>Returns a user's significant views since an instant, deduplicated by item (latest kept).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="sinceUtc">The window start (UTC).</param>
    /// <returns>The events, newest first.</returns>
    IReadOnlyList<PlaybackEvent> GetWeek(Guid userId, DateTime sinceUtc);

    /// <summary>Returns a user's year-to-date totals from the rollup (O(1), survives compaction).</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="year">The calendar year.</param>
    /// <returns>The totals and the date history tracking started.</returns>
    YearToDate GetYearToDate(Guid userId, int year);

    /// <summary>Drops event lines older than 90 days. A no-op (no I/O) unless a month has rolled over.</summary>
    void Compact();

    /// <summary>Starts the background writer and stamps the tracking-start date on first run.</summary>
    void Start();

    /// <summary>Stops the background writer and flushes.</summary>
    /// <returns>A task that completes once draining has stopped.</returns>
    Task StopAsync();
}

/// <summary>A user's year-to-date watch totals.</summary>
/// <param name="Total">Significant views this year.</param>
/// <param name="Completed">Views that counted as completed this year.</param>
/// <param name="TrackingSinceUtc">When Easy Notif started recording history.</param>
public sealed record YearToDate(int Total, int Completed, DateTime TrackingSinceUtc);

/// <summary>On-disk shape of <c>playback-history.json</c>.</summary>
public sealed class PlaybackHistoryFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the recorded events, oldest first.</summary>
    public List<PlaybackEvent> Events { get; init; } = [];

    /// <summary>Gets the per-user/per-year rollup, keyed <c>"{userId:N}:{year}"</c>.</summary>
    public Dictionary<string, RollupYear> Rollup { get; init; } = [];

    /// <summary>Gets or sets when the events were last compacted.</summary>
    public DateTime? LastCompactUtc { get; set; }

    /// <summary>Gets or sets when history tracking started (first run).</summary>
    public DateTime TrackingSinceUtc { get; set; }
}

/// <summary>Rollup for one user and one year.</summary>
public sealed class RollupYear
{
    /// <summary>Gets or sets the significant view count.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the completed view count.</summary>
    public int Completed { get; set; }

    /// <summary>Gets the per-month breakdown, keyed <c>"01".."12"</c>.</summary>
    public Dictionary<string, RollupMonth> ByMonth { get; init; } = [];
}

/// <summary>Rollup for one month.</summary>
public sealed class RollupMonth
{
    /// <summary>Gets or sets the significant view count.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the completed view count.</summary>
    public int Completed { get; set; }
}

/// <inheritdoc cref="IPlaybackHistoryStore"/>
public sealed class PlaybackHistoryStore : JsonFileStore<PlaybackHistoryFile>, IPlaybackHistoryStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const int MaxEvents = 50000;

    private readonly Channel<PlaybackEvent> _channel =
        Channel.CreateUnbounded<PlaybackEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _drain;

    /// <summary>Initializes a new instance of the <see cref="PlaybackHistoryStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public PlaybackHistoryStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<PlaybackHistoryStore> logger)
        : this(applicationPaths, fileSystem, logger, () => DateTime.UtcNow, Task.Delay)
    {
    }

    internal PlaybackHistoryStore(
        IApplicationPaths applicationPaths,
        IFileSystem fileSystem,
        ILogger<PlaybackHistoryStore> logger,
        Func<DateTime> now,
        Func<TimeSpan, CancellationToken, Task> delay)
        : base(applicationPaths, fileSystem, logger, "playback-history.json")
    {
        _now = now;
        _delay = delay;
    }

    /// <inheritdoc/>
    public void Record(PlaybackEvent playbackEvent) => _channel.Writer.TryWrite(playbackEvent);

    /// <inheritdoc/>
    public IReadOnlyList<PlaybackEvent> GetWeek(Guid userId, DateTime sinceUtc)
    {
        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        return Read(file => (IReadOnlyList<PlaybackEvent>)file.Events
            .Where(e => e.UserId == userId && e.Ts >= since)
            .GroupBy(e => e.ItemId)
            .Select(g => g.OrderByDescending(x => x.Ts).First())
            .OrderByDescending(e => e.Ts)
            .ToList());
    }

    /// <inheritdoc/>
    public YearToDate GetYearToDate(Guid userId, int year)
        => Read(file =>
        {
            file.Rollup.TryGetValue(RollupKey(userId, year), out var y);
            return new YearToDate(y?.Total ?? 0, y?.Completed ?? 0, file.TrackingSinceUtc);
        });

    /// <inheritdoc/>
    public void Compact()
    {
        var now = _now();
        var lastCompact = Read(file => file.LastCompactUtc);
        if (lastCompact is { } last && last.Year == now.Year && last.Month == now.Month)
        {
            return;
        }

        var cutoff = now - Retention;
        Mutate(file =>
        {
            file.Events.RemoveAll(e => e.Ts < cutoff);
            file.LastCompactUtc = now;
            return true;
        });
    }

    /// <inheritdoc/>
    public void Start()
    {
        _drain ??= Task.Run(() => DrainLoopAsync(_cts.Token));
        Mutate(file =>
        {
            var changed = false;
            if (file.TrackingSinceUtc == default)
            {
                file.TrackingSinceUtc = _now();
                changed = true;
            }

            if (CollapseDuplicates(file))
            {
                RebuildRollup(file);
                changed = true;
            }
            else if (file.Rollup.Count == 0 && file.Events.Count > 0)
            {
                RebuildRollup(file);
                changed = true;
            }

            return changed;
        });
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_drain is not null)
        {
            try
            {
                await _drain.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task DrainLoopAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Let a burst of stops accumulate before one write.
            try
            {
                await _delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            var batch = new List<PlaybackEvent>();
            while (_channel.Reader.TryRead(out var ev))
            {
                batch.Add(ev);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            var cutoff = _now() - Retention;
            Mutate(file =>
            {
                file.Events.RemoveAll(e => e.Ts < cutoff);
                foreach (var ev in batch)
                {
                    Ingest(file, ev);
                }

                if (file.Events.Count > MaxEvents)
                {
                    file.Events.RemoveRange(0, file.Events.Count - MaxEvents);
                }

                return true;
            });
        }
    }

    /// <summary>
    /// Adds one event, treating a second <c>PlaybackStopped</c> for the same user, item and day as
    /// the same view: the stored row is updated in place and the rollup is not incremented again.
    /// Jellyfin's <c>ISessionManager.PlaybackStopped</c> can fire more than once for a single stop
    /// (observed twice for auto-advancing episodes).
    /// </summary>
    private static void Ingest(PlaybackHistoryFile file, PlaybackEvent ev)
    {
        var index = file.Events.FindLastIndex(e =>
            e.UserId == ev.UserId && e.ItemId == ev.ItemId && e.Ts.Date == ev.Ts.Date);

        if (index < 0)
        {
            file.Events.Add(ev);
            ApplyToRollup(file, ev);
            return;
        }

        var merged = ev with { Completed = file.Events[index].Completed || ev.Completed };
        RemoveFromRollup(file, file.Events[index]);
        file.Events[index] = merged;
        ApplyToRollup(file, merged);
    }

    /// <summary>
    /// Collapses same-user/item/day duplicate rows left by an earlier double-fire (before this
    /// store deduplicated on write). Returns true when it changed the list.
    /// </summary>
    private static bool CollapseDuplicates(PlaybackHistoryFile file)
    {
        var byKey = new Dictionary<(Guid User, Guid Item, DateOnly Day), PlaybackEvent>();
        foreach (var ev in file.Events)
        {
            var key = (ev.UserId, ev.ItemId, DateOnly.FromDateTime(ev.Ts));
            byKey[key] = byKey.TryGetValue(key, out var prev)
                ? ev with { Completed = prev.Completed || ev.Completed }
                : ev;
        }

        if (byKey.Count == file.Events.Count)
        {
            return false;
        }

        var collapsed = byKey.Values.OrderBy(e => e.Ts).ToList();
        file.Events.Clear();
        file.Events.AddRange(collapsed);
        return true;
    }

    private static void RemoveFromRollup(PlaybackHistoryFile file, PlaybackEvent ev)
    {
        if (!file.Rollup.TryGetValue(RollupKey(ev.UserId, ev.Ts.Year), out var year))
        {
            return;
        }

        year.Total = Math.Max(0, year.Total - 1);
        if (ev.Completed)
        {
            year.Completed = Math.Max(0, year.Completed - 1);
        }

        var month = ev.Ts.ToString("MM", CultureInfo.InvariantCulture);
        if (year.ByMonth.TryGetValue(month, out var monthStats))
        {
            monthStats.Total = Math.Max(0, monthStats.Total - 1);
            if (ev.Completed)
            {
                monthStats.Completed = Math.Max(0, monthStats.Completed - 1);
            }
        }
    }

    private static void ApplyToRollup(PlaybackHistoryFile file, PlaybackEvent ev)
    {
        var key = RollupKey(ev.UserId, ev.Ts.Year);
        if (!file.Rollup.TryGetValue(key, out var year))
        {
            year = new RollupYear();
            file.Rollup[key] = year;
        }

        year.Total++;
        if (ev.Completed)
        {
            year.Completed++;
        }

        var month = ev.Ts.ToString("MM", CultureInfo.InvariantCulture);
        if (!year.ByMonth.TryGetValue(month, out var monthStats))
        {
            monthStats = new RollupMonth();
            year.ByMonth[month] = monthStats;
        }

        monthStats.Total++;
        if (ev.Completed)
        {
            monthStats.Completed++;
        }
    }

    private static void RebuildRollup(PlaybackHistoryFile file)
    {
        file.Rollup.Clear();
        foreach (var ev in file.Events)
        {
            ApplyToRollup(file, ev);
        }
    }

    private static string RollupKey(Guid userId, int year)
        => $"{userId:N}:{year.ToString(CultureInfo.InvariantCulture)}";

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
