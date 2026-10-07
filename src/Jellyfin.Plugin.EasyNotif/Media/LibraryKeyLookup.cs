using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Finds which media keys already exist in the library under another id: a replaced, renamed or
/// moved file, or a second copy, must not be announced as new.
/// </summary>
internal static class LibraryKeyLookup
{
    /// <summary>
    /// Returns the keys of <paramref name="byKey"/> present in the library under an id outside
    /// <paramref name="candidateIds"/>: one query per series touched (its real episodes) and one per
    /// movie with a provider id. Virtual items (the missing episodes Jellyfin lists ahead of the
    /// file) never count as present.
    /// </summary>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="byKey">The candidate items by media key.</param>
    /// <param name="candidateIds">The ids of the candidates themselves.</param>
    /// <returns>The keys found under another id.</returns>
    public static HashSet<string> PresentOutside(
        ILibraryManager libraryManager,
        IReadOnlyDictionary<string, BaseItem> byKey,
        IReadOnlySet<Guid> candidateIds)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        var seriesIds = byKey
            .Where(kv => kv.Value is Episode && kv.Key.StartsWith("ep:", StringComparison.Ordinal))
            .Select(kv => ((Episode)kv.Value).SeriesId)
            .Distinct();
        foreach (var seriesId in seriesIds)
        {
            AddPresent(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [seriesId],
                Recursive = true,
                IsVirtualItem = false,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { Fields = [] }
            });
        }

        foreach (var (key, item) in byKey.Where(kv => kv.Key.StartsWith("movie:", StringComparison.Ordinal)))
        {
            var provider = key.StartsWith("movie:tmdb:", StringComparison.Ordinal) ? MetadataProvider.Tmdb : MetadataProvider.Imdb;
            AddPresent(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                HasAnyProviderId = new Dictionary<string, string> { [provider.ToString()] = item.GetProviderId(provider)! },
                Recursive = true,
                IsVirtualItem = false,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { Fields = [] }
            });
        }

        return present;

        void AddPresent(InternalItemsQuery query)
        {
            foreach (var other in libraryManager.GetItemList(query).Where(i => !i.IsVirtualItem && !candidateIds.Contains(i.Id)))
            {
                var key = MediaKey.For(other);
                if (byKey.ContainsKey(key))
                {
                    present.Add(key);
                }
            }
        }
    }
}
