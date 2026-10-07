using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Decides which library items a Jellyfin user may see, applying both the library restrictions
/// (<c>EnableAllFolders</c> / <c>EnabledFolders</c>) and the maximum parental rating, inherited from
/// the series for an episode. Checked at send time, per item and per recipient: ratings can arrive
/// after the item was added.
/// </summary>
public interface IAccessFilter
{
    /// <summary>Returns the subset of <paramref name="itemIds"/> visible to <paramref name="userId"/>.</summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="itemIds">The candidate item ids.</param>
    /// <returns>The visible ids; empty when the user is unknown. Fails closed on any error.</returns>
    IReadOnlySet<Guid> VisibleTo(Guid userId, IReadOnlyCollection<Guid> itemIds);
}

/// <inheritdoc cref="IAccessFilter"/>
/// <remarks>
/// Relies on <c>BaseItem.IsVisibleStandalone(User)</c>, the only 10.11 API measured to honour library
/// access as well as ratings. <c>IsVisible(User)</c> and <c>InternalItemsQuery(user) { ItemIds }</c>
/// ignore library access and must not be used here (TODO_V2 phase 05, DEC-14).
/// </remarks>
public sealed class AccessFilter : IAccessFilter
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly Logging.IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="AccessFilter"/> class.</summary>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public AccessFilter(IUserManager userManager, ILibraryManager libraryManager, Logging.IEasyNotifLog log)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _log = log;
    }

    /// <inheritdoc/>
    public IReadOnlySet<Guid> VisibleTo(Guid userId, IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var visible = new HashSet<Guid>();
        if (itemIds.Count == 0)
        {
            return visible;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            _log.Warn("access.unknown-user", new Dictionary<string, object?> { ["userId"] = userId });
            return visible;
        }

        foreach (var id in itemIds)
        {
            try
            {
                var item = _libraryManager.GetItemById(id);
                if (item is not null && item.IsVisibleStandalone(user))
                {
                    visible.Add(id);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("access.error", new Dictionary<string, object?> { ["userId"] = userId, ["itemId"] = id }, ex);
            }
        }

        return visible;
    }
}
