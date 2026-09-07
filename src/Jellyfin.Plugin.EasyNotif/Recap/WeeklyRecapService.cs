using Jellyfin.Plugin.EasyNotif.Playback;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.EasyNotif.Recap;

/// <summary>
/// Builds the per-user <see cref="RecapModel"/> for the weekly watch recap from the plugin's own
/// playback history (Synthese.md section 1). Read-only: it never touches the library or user data
/// (R12).
/// </summary>
public interface IWeeklyRecapService
{
    /// <summary>Builds the recap for one user as of an instant.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="nowUtc">The reference instant (UTC); the week is the preceding seven days.</param>
    /// <returns>The recap model.</returns>
    RecapModel BuildFor(Guid userId, DateTime nowUtc);
}

/// <inheritdoc cref="IWeeklyRecapService"/>
public sealed class WeeklyRecapService : IWeeklyRecapService
{
    private readonly IPlaybackHistoryStore _history;
    private readonly IUserManager _users;

    /// <summary>Initializes a new instance of the <see cref="WeeklyRecapService"/> class.</summary>
    /// <param name="history">The playback history store.</param>
    /// <param name="users">The Jellyfin user manager (read-only, for the username).</param>
    public WeeklyRecapService(IPlaybackHistoryStore history, IUserManager users)
    {
        _history = history;
        _users = users;
    }

    /// <inheritdoc/>
    public RecapModel BuildFor(Guid userId, DateTime nowUtc)
    {
        var week = _history.GetWeek(userId, nowUtc.AddDays(-7));

        var entries = new List<WatchedEntry>();

        foreach (var movie in week.Where(e => e.Kind == "Movie"))
        {
            entries.Add(new WatchedEntry(RecapKind.Movie, movie.Name, null, 1, movie.Ts, movie.Completed));
        }

        var episodes = week.Where(e => e.Kind == "Episode");
        foreach (var group in episodes.GroupBy(e => e.SeriesId ?? Guid.Empty))
        {
            var items = group.ToList();
            if (group.Key != Guid.Empty && items.Count >= 2)
            {
                entries.Add(new WatchedEntry(
                    RecapKind.Series,
                    items[0].SeriesName ?? items[0].Name,
                    null,
                    items.Count,
                    items.Max(x => x.Ts),
                    items.Any(x => x.Completed)));
            }
            else
            {
                foreach (var episode in items)
                {
                    entries.Add(new WatchedEntry(
                        RecapKind.Episode,
                        episode.Name,
                        episode.SeriesName,
                        1,
                        episode.Ts,
                        episode.Completed));
                }
            }
        }

        var watched = entries.OrderByDescending(e => e.WhenUtc).ToList();

        var ytd = _history.GetYearToDate(userId, nowUtc.Year);
        var yearStart = new DateTime(nowUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var partialSince = ytd.TrackingSinceUtc > yearStart ? ytd.TrackingSinceUtc : (DateTime?)null;

        var name = _users.GetUserById(userId)?.Username;

        return new RecapModel(watched, ytd.Total, ytd.Completed, partialSince, name);
    }
}
