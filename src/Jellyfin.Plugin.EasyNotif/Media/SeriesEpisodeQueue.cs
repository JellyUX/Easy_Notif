using System.Threading.Channels;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Feeds <see cref="ISeriesNotifyStore"/> from the library events: a new real episode of a followed
/// series is queued, a removed one leaves the queue and blocks its key for a day (an upgrade replaces
/// the file under a new id), and a removed followed series is purged with its subscriptions. The
/// event handlers only test an in-memory index and write to a channel; a background drain applies
/// the events in order (R13). See <see cref="SeriesEpisodeQueue"/>.
/// </summary>
public interface ISeriesEpisodeQueue
{
    /// <summary>Handles an <c>ItemAdded</c> event. Non-blocking.</summary>
    /// <param name="item">The added item.</param>
    void OnAdded(BaseItem item);

    /// <summary>Handles an <c>ItemRemoved</c> event. Non-blocking.</summary>
    /// <param name="item">The removed item.</param>
    void OnRemoved(BaseItem item);

    /// <summary>Starts the background writer.</summary>
    void Start();

    /// <summary>Stops the background writer and flushes.</summary>
    /// <returns>A task that completes once draining has stopped.</returns>
    Task StopAsync();
}

/// <inheritdoc cref="ISeriesEpisodeQueue"/>
public sealed class SeriesEpisodeQueue : ISeriesEpisodeQueue, IDisposable
{
    private readonly Channel<SeriesEvent> _channel =
        Channel.CreateBounded<SeriesEvent>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    private readonly ISeriesNotifyStore _store;
    private readonly ILogger<SeriesEpisodeQueue> _logger;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _drain;

    /// <summary>Initializes a new instance of the <see cref="SeriesEpisodeQueue"/> class.</summary>
    /// <param name="store">The series notification store.</param>
    /// <param name="logger">Logger.</param>
    public SeriesEpisodeQueue(ISeriesNotifyStore store, ILogger<SeriesEpisodeQueue> logger)
        : this(store, logger, () => DateTime.UtcNow, Task.Delay)
    {
    }

    internal SeriesEpisodeQueue(
        ISeriesNotifyStore store,
        ILogger<SeriesEpisodeQueue> logger,
        Func<DateTime> now,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _store = store;
        _logger = logger;
        _now = now;
        _delay = delay;
    }

    /// <inheritdoc/>
    public void OnAdded(BaseItem item)
    {
        // SeriesId is still empty when ItemAdded fires (DEC-13); the parent chain already holds it.
        if (item is Episode { IsVirtualItem: false } episode
            && SeriesOf(episode) is { } seriesId
            && _store.IsFollowed(seriesId))
        {
            _channel.Writer.TryWrite(new SeriesEvent(false, episode.Id, seriesId, null));
        }
    }

    /// <inheritdoc/>
    public void OnRemoved(BaseItem item)
    {
        // A removed episode still carries its series and numbers, so its key is computed now.
        if (item is Episode episode
            && SeriesOf(episode) is { } seriesId
            && _store.IsFollowed(seriesId))
        {
            _channel.Writer.TryWrite(new SeriesEvent(true, episode.Id, seriesId, KeyOf(episode)));
        }
        else if (item is Series series && _store.IsFollowed(series.Id))
        {
            // A deleted series takes its subscriptions with it (SEC-11).
            _channel.Writer.TryWrite(new SeriesEvent(true, series.Id, series.Id, null, WholeSeries: true));
        }
    }

    /// <inheritdoc/>
    public void Start() => _drain ??= Task.Run(() => DrainLoopAsync(_cts.Token));

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_drain is not null)
        {
            await _drain.ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _cts.Dispose();

    private static Guid? SeriesOf(Episode episode)
    {
        if (episode.SeriesId != Guid.Empty)
        {
            return episode.SeriesId;
        }

        try
        {
            return episode.FindParent<Series>()?.Id;
        }
        catch (Exception)
        {
            // An event handler must never throw into Jellyfin; an unresolved series is not followed.
            return null;
        }
    }

    private static string? KeyOf(Episode episode)
    {
        var key = MediaKey.For(episode);
        return key.StartsWith("ep:", StringComparison.Ordinal) ? key : null;
    }

    private async Task DrainLoopAsync(CancellationToken cancellationToken)
    {
        // Not cancelled on stop: completing the writer ends the loop once the queued events are
        // written, so a shutdown inside the batching delay loses nothing.
        while (await _channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            // Let a burst of events (a season copied at once) accumulate before writing.
            try
            {
                await _delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            var batch = new List<SeriesEvent>();
            while (_channel.Reader.TryRead(out var evt))
            {
                batch.Add(evt);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                Apply(batch, _now());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A transient write failure must not kill the drain: log the lost batch and keep going.
                _logger.LogError(ex, "[EasyNotif] Failed to record {Count} series episode event(s); the drain continues.", batch.Count);
            }
        }
    }

    // Applied in arrival order: a move fires ItemAdded for the new id before ItemRemoved for the old
    // one, and a later removal must win over an earlier addition of the same item.
    private void Apply(List<SeriesEvent> batch, DateTime now)
    {
        var adds = new List<(Guid, Guid)>();
        foreach (var evt in batch)
        {
            if (!evt.Removed)
            {
                adds.Add((evt.ItemId, evt.SeriesId));
                continue;
            }

            if (adds.Count > 0)
            {
                _store.Enqueue(adds, now);
                adds.Clear();
            }

            if (evt.WholeSeries)
            {
                _store.PurgeSeries(evt.SeriesId);
            }
            else
            {
                _store.RecordRemoved(evt.ItemId, evt.Key, now);
            }
        }

        if (adds.Count > 0)
        {
            _store.Enqueue(adds, now);
        }
    }

    private sealed record SeriesEvent(bool Removed, Guid ItemId, Guid SeriesId, string? Key, bool WholeSeries = false);
}
