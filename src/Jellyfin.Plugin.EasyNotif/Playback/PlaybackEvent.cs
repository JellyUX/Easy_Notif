namespace Jellyfin.Plugin.EasyNotif.Playback;

/// <summary>
/// One significant playback stop recorded by Easy Notif (Synthese.md section 6). The plugin keeps
/// its own lightweight history because Jellyfin core has no usable timestamped watch log
/// (Synthese.md section 2.4).
/// </summary>
/// <param name="Ts">The UTC server time the playback stopped.</param>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="ItemId">The played item id.</param>
/// <param name="Kind">"Movie" or "Episode".</param>
/// <param name="SeriesId">The parent series id for an episode; null for a movie.</param>
/// <param name="Name">The item name (episode title or movie title).</param>
/// <param name="SeriesName">The parent series name for an episode; null for a movie.</param>
/// <param name="PositionTicks">The stop position, in ticks.</param>
/// <param name="RuntimeTicks">The item runtime, in ticks (0 when unknown).</param>
/// <param name="Completed">Whether the view counts as completed (played to completion, or past
/// 20 minutes, or past 90 percent of the runtime).</param>
public sealed record PlaybackEvent(
    DateTime Ts,
    Guid UserId,
    Guid ItemId,
    string Kind,
    Guid? SeriesId,
    string Name,
    string? SeriesName,
    long PositionTicks,
    long RuntimeTicks,
    bool Completed);
