using Jellyfin.Plugin.EasyNotif.Playback;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="IPlaybackHistoryStore"/> for tests: <see cref="Record"/> appends to
/// <see cref="Recorded"/>, the week/year queries read seeded events, and start/stop/compact just
/// count their calls.
/// </summary>
public sealed class FakePlaybackHistoryStore : IPlaybackHistoryStore
{
    private readonly List<PlaybackEvent> _events = [];

    /// <summary>Gets the events passed to <see cref="Record"/>, in order.</summary>
    public List<PlaybackEvent> Recorded { get; } = [];

    /// <summary>Gets the number of times <see cref="Compact"/> was called.</summary>
    public int CompactCount { get; private set; }

    /// <summary>Gets the number of times <see cref="Start"/> was called.</summary>
    public int StartCount { get; private set; }

    /// <summary>Gets the number of times <see cref="StopAsync"/> was called.</summary>
    public int StopCount { get; private set; }

    /// <summary>Gets or sets the totals returned by <see cref="GetYearToDate"/>.</summary>
    public YearToDate YearToDate { get; set; } = new(0, 0, default);

    /// <summary>Seeds an event visible to <see cref="GetWeek"/>.</summary>
    /// <param name="playbackEvent">The event.</param>
    public void Seed(PlaybackEvent playbackEvent) => _events.Add(playbackEvent);

    /// <inheritdoc/>
    public void Record(PlaybackEvent playbackEvent)
    {
        Recorded.Add(playbackEvent);
        _events.Add(playbackEvent);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PlaybackEvent> GetWeek(Guid userId, DateTime sinceUtc)
        => _events
            .Where(e => e.UserId == userId && e.Ts >= sinceUtc)
            .GroupBy(e => e.ItemId)
            .Select(g => g.OrderByDescending(x => x.Ts).First())
            .OrderByDescending(e => e.Ts)
            .ToList();

    /// <inheritdoc/>
    public YearToDate GetYearToDate(Guid userId, int year) => YearToDate;

    /// <inheritdoc/>
    public void Compact() => CompactCount++;

    /// <summary>Gets the user ids passed to <see cref="Purge"/>, in order.</summary>
    public List<Guid> Purged { get; } = [];

    /// <inheritdoc/>
    public void Purge(Guid userId)
    {
        Purged.Add(userId);
        _events.RemoveAll(e => e.UserId == userId);
    }

    /// <inheritdoc/>
    public void Start() => StartCount++;

    /// <inheritdoc/>
    public Task StopAsync()
    {
        StopCount++;
        return Task.CompletedTask;
    }
}
