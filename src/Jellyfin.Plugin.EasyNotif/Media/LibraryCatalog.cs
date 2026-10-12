using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>A library a user can pick for the newsletter (FEAT-02).</summary>
/// <param name="Id">The library (collection folder) id.</param>
/// <param name="Name">The library name.</param>
public sealed record LibraryInfo(Guid Id, string Name);

/// <summary>
/// Read-only view of the Jellyfin libraries behind the per-user newsletter library choice
/// (FEAT-02, DEC-74, DEC-75).
/// </summary>
public interface ILibraryCatalog
{
    /// <summary>
    /// Returns the movie, TV show and mixed libraries <paramref name="userId"/> may open, the only
    /// ones that feed the newsletter. Empty for an unknown user or on error.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The libraries, in Jellyfin order.</returns>
    IReadOnlyList<LibraryInfo> NewsletterLibrariesFor(Guid userId);

    /// <summary>Returns the ids of the libraries an item belongs to; empty when unknown.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>The library ids.</returns>
    IReadOnlyList<Guid> LibrariesOf(BaseItem item);
}

/// <inheritdoc cref="ILibraryCatalog"/>
/// <remarks>
/// The user's libraries come from the children of the user root folder, the list Jellyfin itself
/// checks for library access (<c>EnableAllFolders</c> / <c>EnabledFolders</c>).
/// </remarks>
public sealed class LibraryCatalog : ILibraryCatalog
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly Logging.IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="LibraryCatalog"/> class.</summary>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public LibraryCatalog(IUserManager userManager, ILibraryManager libraryManager, Logging.IEasyNotifLog log)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _log = log;
    }

    /// <inheritdoc/>
    public IReadOnlyList<LibraryInfo> NewsletterLibrariesFor(Guid userId)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                return [];
            }

            return _libraryManager.GetUserRootFolder()
                .GetChildren(user, true)
                .OfType<CollectionFolder>()
                .Where(f => f.CollectionType is null or CollectionType.movies or CollectionType.tvshows)
                .Select(f => new LibraryInfo(f.Id, f.Name ?? string.Empty))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Warn("libraries.error", new Dictionary<string, object?> { ["userId"] = userId }, ex);
            return [];
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<Guid> LibrariesOf(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            return _libraryManager.GetCollectionFolders(item).Select(f => f.Id).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn("libraries.error", new Dictionary<string, object?> { ["itemId"] = item.Id }, ex);
            return [];
        }
    }
}
