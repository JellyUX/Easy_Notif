using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.EasyNotif.Playback;

/// <summary>
/// Seeds, once, the yearly total with the views made in the current year before Easy Notif started
/// tracking (FEAT-08). See <see cref="YearTotalSeeder"/>.
/// </summary>
public interface IYearTotalSeeder
{
    /// <summary>Runs the seed if it has not run yet; a no-op afterwards.</summary>
    /// <param name="nowUtc">The current instant (UTC).</param>
    void RunOnce(DateTime nowUtc);
}

/// <inheritdoc cref="IYearTotalSeeder"/>
/// <remarks>
/// DEC-81: every movie and episode a user has marked played, whose <c>LastPlayedDate</c> falls between
/// the first of the year and <see cref="YearSeedState.TrackingSinceUtc"/>, counts as one completed
/// view; never the detail of the views, and rewatches are lost (Jellyfin keeps the last date only),
/// hence the "estimate" wording of the recap (DEC-80). Tracking that started before this year has
/// nothing to seed. One library query per user, once; read-only.
/// </remarks>
public sealed class YearTotalSeeder : IYearTotalSeeder
{
    private readonly IPlaybackHistoryStore _history;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly Logging.IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="YearTotalSeeder"/> class.</summary>
    /// <param name="history">The playback history store.</param>
    /// <param name="users">The Jellyfin user manager.</param>
    /// <param name="library">The Jellyfin library manager (read-only use).</param>
    /// <param name="userData">The Jellyfin user data manager (read-only use).</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public YearTotalSeeder(
        IPlaybackHistoryStore history,
        IUserManager users,
        ILibraryManager library,
        IUserDataManager userData,
        Logging.IEasyNotifLog log)
    {
        _history = history;
        _users = users;
        _library = library;
        _userData = userData;
        _log = log;
    }

    /// <inheritdoc/>
    public void RunOnce(DateTime nowUtc)
    {
        var state = _history.GetSeedState();
        if (state.SeededUtc is not null || state.TrackingSinceUtc == default)
        {
            return;
        }

        var year = nowUtc.Year;
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dates = new Dictionary<Guid, IReadOnlyList<DateTime>>();
        if (state.TrackingSinceUtc > yearStart)
        {
            foreach (var user in _users.GetUsers())
            {
                var played = _library.GetItemList(new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                        Recursive = true,
                        IsPlayed = true,
                        DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { Fields = [] }
                    })
                    .Select(item => _userData.GetUserData(user, item))
                    .Where(d => d is { Played: true, LastPlayedDate: { } last } && last >= yearStart && last < state.TrackingSinceUtc)
                    .Select(d => DateTime.SpecifyKind(d!.LastPlayedDate!.Value, DateTimeKind.Utc))
                    .ToList();
                if (played.Count > 0)
                {
                    dates[user.Id] = played;
                }
            }
        }

        if (_history.ApplyYearSeed(year, dates, nowUtc))
        {
            _log.Info("history.seeded", new Dictionary<string, object?>
            {
                ["year"] = year,
                ["users"] = dates.Count,
                ["views"] = dates.Values.Sum(d => d.Count),
                ["before"] = state.TrackingSinceUtc
            });
        }
    }
}
