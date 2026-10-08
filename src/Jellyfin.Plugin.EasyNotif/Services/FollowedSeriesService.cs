using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.EasyNotif.Services;

/// <summary>A series the user follows, as listed to that user.</summary>
/// <param name="Id">The series id.</param>
/// <param name="Name">The series name.</param>
/// <param name="HasImage">Whether the series has a primary image.</param>
public sealed record FollowedSeries(Guid Id, string Name, bool HasImage);

/// <summary>
/// The caller-facing side of series subscriptions. A series counts only when it exists and is visible
/// to the user (library access and parental rating, <see cref="IAccessFilter"/>); otherwise every
/// method answers the same "not found" so a user cannot probe for series they cannot see. A visible
/// series with some episodes above the user's rating is still followable: those episodes are dropped
/// at send time.
/// </summary>
public interface IFollowedSeriesService
{
    /// <summary>Lists the series the user follows and can still see, by name.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The followed series.</returns>
    IReadOnlyList<FollowedSeries> List(Guid userId);

    /// <summary>Returns whether the user follows the series.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="seriesId">The series id.</param>
    /// <returns>The state, or null when the series does not exist or is not visible to the user.</returns>
    bool? IsFollowing(Guid userId, Guid seriesId);

    /// <summary>Follows the series; idempotent.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="seriesId">The series id.</param>
    /// <param name="source">How the subscription was made (<c>bell</c>, <c>panel</c>, <c>email</c>).</param>
    /// <returns>False when the series does not exist or is not visible to the user.</returns>
    bool Follow(Guid userId, Guid seriesId, string source);

    /// <summary>
    /// Stops following the series; idempotent. A series the user already follows can always be
    /// dropped, even after losing access to it.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="seriesId">The series id.</param>
    /// <returns>False when the user does not follow the series and it is not visible to them.</returns>
    bool Unfollow(Guid userId, Guid seriesId);
}

/// <inheritdoc cref="IFollowedSeriesService"/>
public sealed class FollowedSeriesService : IFollowedSeriesService
{
    private readonly ISeriesNotifyStore _store;
    private readonly IAccessFilter _access;
    private readonly ILibraryManager _libraryManager;
    private readonly TimeProvider _time;

    /// <summary>Initializes a new instance of the <see cref="FollowedSeriesService"/> class.</summary>
    /// <param name="store">The series notification store.</param>
    /// <param name="access">The per-user access filter.</param>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="time">The clock.</param>
    public FollowedSeriesService(ISeriesNotifyStore store, IAccessFilter access, ILibraryManager libraryManager, TimeProvider time)
    {
        _store = store;
        _access = access;
        _libraryManager = libraryManager;
        _time = time;
    }

    /// <inheritdoc/>
    public IReadOnlyList<FollowedSeries> List(Guid userId)
    {
        var followed = Followed(userId);
        var visible = _access.VisibleTo(userId, followed);
        return followed
            .Where(visible.Contains)
            .Select(id => _libraryManager.GetItemById(id) as Series)
            .OfType<Series>()
            .Select(s => new FollowedSeries(s.Id, s.Name, s.HasImage(ImageType.Primary, 0)))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <inheritdoc/>
    public bool? IsFollowing(Guid userId, Guid seriesId)
        => IsVisibleSeries(userId, seriesId) ? Followed(userId).Contains(seriesId) : null;

    /// <inheritdoc/>
    public bool Follow(Guid userId, Guid seriesId, string source)
    {
        if (!IsVisibleSeries(userId, seriesId))
        {
            return false;
        }

        _store.Subscribe(userId, seriesId, source, _time.GetUtcNow().UtcDateTime);
        return true;
    }

    /// <inheritdoc/>
    public bool Unfollow(Guid userId, Guid seriesId)
    {
        if (!Followed(userId).Contains(seriesId) && !IsVisibleSeries(userId, seriesId))
        {
            return false;
        }

        _store.Unsubscribe(userId, seriesId);
        return true;
    }

    private List<Guid> Followed(Guid userId)
        => [.. _store.Subscriptions().Where(s => s.UserId == userId).Select(s => s.SeriesId)];

    private bool IsVisibleSeries(Guid userId, Guid seriesId)
        => seriesId != Guid.Empty
            && _libraryManager.GetItemById(seriesId) is Series
            && _access.VisibleTo(userId, [seriesId]).Contains(seriesId);
}
