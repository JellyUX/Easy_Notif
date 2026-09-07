using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.EasyNotif.Playback;

/// <summary>
/// Pure decision helper that turns a raw playback stop into a <see cref="PlaybackEvent"/>, or
/// rejects it. Kept separate from the <c>ISessionManager.PlaybackStopped</c> subscription in
/// <c>StartupService</c> so the filtering rules can be tested without a real event args instance.
/// </summary>
public static class PlaybackEventFactory
{
    private static readonly long MinPositionTicks = TimeSpan.FromMinutes(3).Ticks;
    private static readonly long CompletedPositionTicks = TimeSpan.FromMinutes(20).Ticks;

    /// <summary>
    /// Builds a <see cref="PlaybackEvent"/> for a significant playback stop of a movie or episode.
    /// </summary>
    /// <param name="item">The played item (only <see cref="Movie"/> and <see cref="Episode"/> count).</param>
    /// <param name="positionTicks">The stop position, in ticks (null treated as 0).</param>
    /// <param name="playedToCompletion">Whether Jellyfin flagged the stop as played to completion.</param>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="nowUtc">The current UTC time (the event timestamp).</param>
    /// <param name="evt">The built event when this returns true; otherwise <c>default</c>.</param>
    /// <returns>True when the stop is worth recording.</returns>
    public static bool TryBuild(
        BaseItem? item,
        long? positionTicks,
        bool playedToCompletion,
        Guid userId,
        DateTime nowUtc,
        out PlaybackEvent evt)
    {
        evt = default!;
        if (item is not (Movie or Episode))
        {
            return false;
        }

        var position = positionTicks ?? 0;
        if (position < MinPositionTicks)
        {
            return false;
        }

        var runtime = item.RunTimeTicks ?? 0;
        var completed = playedToCompletion
            || position >= CompletedPositionTicks
            || (runtime > 0 && position >= (long)(runtime * 0.9));

        var episode = item as Episode;
        evt = new PlaybackEvent(
            nowUtc,
            userId,
            item.Id,
            episode is null ? "Movie" : "Episode",
            episode?.SeriesId,
            item.Name ?? string.Empty,
            episode?.SeriesName,
            position,
            runtime,
            completed);
        return true;
    }
}
