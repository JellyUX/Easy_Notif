using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Media;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.EasyNotif.Recap;

/// <summary>One started, unfinished movie or episode of the recap's "pick up where you left off" section.</summary>
/// <param name="Title">The movie or episode title.</param>
/// <param name="SeriesTitle">The series title for an episode; null for a movie.</param>
/// <param name="EpisodeLabel">The <c>S01E03</c> label of an episode, when both numbers are known.</param>
/// <param name="RemainingMinutes">The minutes left (rounded up), or null when the runtime is unknown.</param>
/// <param name="PosterUrl">The poster link (server image or <c>cid:</c>), or null.</param>
/// <param name="DetailUrl">The item's web page, or null without a public server URL.</param>
public sealed record ResumeEntry(
    string Title,
    string? SeriesTitle,
    string? EpisodeLabel,
    int? RemainingMinutes,
    string? PosterUrl,
    string? DetailUrl);

/// <summary>The "pick up where you left off" section of one user's recap (FEAT-03).</summary>
/// <param name="Entries">At most <see cref="ResumeLookup.MaxEntries"/> entries, most recently played first.</param>
/// <param name="Posters">The inline posters the entries reference; empty with a public server URL.</param>
public sealed record ResumeSection(IReadOnlyList<ResumeEntry> Entries, IReadOnlyList<EmailAttachment> Posters)
{
    /// <summary>Gets an empty section.</summary>
    public static ResumeSection Empty { get; } = new([], []);
}

/// <summary>Finds the movies and episodes a user started and did not finish (FEAT-03).</summary>
public interface IResumeLookup
{
    /// <summary>Returns the user's section as of an instant; empty for an unknown user or on error.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="nowUtc">The reference instant (UTC).</param>
    /// <returns>The section.</returns>
    ResumeSection For(Guid userId, DateTime nowUtc);
}

/// <inheritdoc cref="IResumeLookup"/>
/// <remarks>
/// DEC-78: started (position above 0), not played to the end, last played within
/// <see cref="Window"/>, visible to the user (<see cref="IAccessFilter"/>, as the
/// <c>InternalItemsQuery(user)</c> query ignores library access), most recent first. Posters follow
/// the newsletter (DEC-77): the series poster for an episode, linked with a public server URL,
/// attached inline otherwise. Read-only.
/// </remarks>
public sealed class ResumeLookup : IResumeLookup
{
    /// <summary>The largest number of entries in the section.</summary>
    public const int MaxEntries = 3;

    /// <summary>How many resumable items are read before filtering.</summary>
    public const int QueryLimit = 30;

    /// <summary>How far back the last playback may be.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IAccessFilter _access;
    private readonly IPosterCache _posterCache;
    private readonly IConfigAccessor _config;
    private readonly ServerLinkContext _links;
    private readonly Logging.IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="ResumeLookup"/> class.</summary>
    /// <param name="users">The Jellyfin user manager.</param>
    /// <param name="library">The Jellyfin library manager (read-only use).</param>
    /// <param name="userData">The Jellyfin user data manager (read-only use).</param>
    /// <param name="access">The per-user access filter.</param>
    /// <param name="posterCache">The inline poster cache (used only when no public URL is set).</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="links">The captured server identity for deep links.</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public ResumeLookup(
        IUserManager users,
        ILibraryManager library,
        IUserDataManager userData,
        IAccessFilter access,
        IPosterCache posterCache,
        IConfigAccessor config,
        ServerLinkContext links,
        Logging.IEasyNotifLog log)
    {
        _users = users;
        _library = library;
        _userData = userData;
        _access = access;
        _posterCache = posterCache;
        _config = config;
        _links = links;
        _log = log;
    }

    /// <inheritdoc/>
    public ResumeSection For(Guid userId, DateTime nowUtc)
    {
        try
        {
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return ResumeSection.Empty;
            }

            var candidates = _library.GetItemList(new InternalItemsQuery(user)
            {
                IsResumable = true,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
                Limit = QueryLimit,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { Fields = [] }
            });
            if (candidates.Count == 0)
            {
                return ResumeSection.Empty;
            }

            var visible = _access.VisibleTo(userId, candidates.Select(i => i.Id).ToList());
            var since = nowUtc - Window;
            var picked = candidates
                .Where(i => visible.Contains(i.Id))
                .Select(i => (Item: i, Data: _userData.GetUserData(user, i)))
                .Where(x => x.Data is { Played: false, PlaybackPositionTicks: > 0 }
                    && x.Data.LastPlayedDate is { } last && last >= since)
                .OrderByDescending(x => x.Data!.LastPlayedDate)
                .Take(MaxEntries)
                .ToList();
            if (picked.Count == 0)
            {
                return ResumeSection.Empty;
            }

            var baseUrl = MediaLinks.BaseUrl(_config.Get().PublicServerUrl);
            var posterItems = picked.Select(x => PosterItem(x.Item)).ToList();

            // Render is synchronous; PosterCache awaits with ConfigureAwait(false) and neither the
            // scheduled task nor ASP.NET Core has a synchronization context, so this cannot deadlock.
            var inline = MediaLinks.InlinePostersAsync(baseUrl, _posterCache, posterItems.DistinctBy(i => i.Id), CancellationToken.None)
                .GetAwaiter().GetResult();

            var entries = picked
                .Select((x, index) => new ResumeEntry(
                    x.Item.Name ?? string.Empty,
                    (x.Item as Episode)?.SeriesName,
                    EpisodeLabel(x.Item as Episode),
                    RemainingMinutes(x.Item.RunTimeTicks, x.Data!.PlaybackPositionTicks),
                    MediaLinks.PosterUrl(baseUrl, inline, posterItems[index]),
                    MediaLinks.DetailUrl(baseUrl, _links.SystemId, x.Item.Id)))
                .ToList();
            return new ResumeSection(entries, [.. inline.Values]);
        }
        catch (Exception ex)
        {
            _log.Warn("recap.resume-error", new Dictionary<string, object?> { ["userId"] = userId }, ex);
            return ResumeSection.Empty;
        }
    }

    /// <summary>Returns the minutes left, rounded up, or null when the runtime is unknown.</summary>
    /// <param name="runTimeTicks">The item runtime.</param>
    /// <param name="positionTicks">The playback position.</param>
    /// <returns>The minutes left.</returns>
    public static int? RemainingMinutes(long? runTimeTicks, long positionTicks)
        => runTimeTicks is long total && total > 0
            ? (int)Math.Ceiling(Math.Max(0, total - positionTicks) / (double)TimeSpan.TicksPerMinute)
            : null;

    private static string? EpisodeLabel(Episode? episode)
        => episode is { ParentIndexNumber: { } season, IndexNumber: { } number }
            ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{number:00}")
            : null;

    // The series poster for an episode (episode stills are landscape), the item itself otherwise.
    // Resolved by id, as the newsletter does: Episode.Series goes through Jellyfin's static manager.
    private BaseItem PosterItem(BaseItem item)
        => item is Episode { SeriesId: var seriesId } && seriesId != Guid.Empty && _library.GetItemById(seriesId) is Series series
            ? series
            : item;
}
