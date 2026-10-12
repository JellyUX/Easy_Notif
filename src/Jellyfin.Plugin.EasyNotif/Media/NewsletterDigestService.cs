using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Reads the movies and episodes added to the library since a given instant and shapes them into a
/// <see cref="NewsletterDigest"/>. Read-only: it only calls <see cref="ILibraryManager.GetItemList(InternalItemsQuery)"/>
/// (R12). See <see cref="NewsletterDigestService"/>.
/// </summary>
public interface INewsletterDigestService
{
    /// <summary>Builds the digest of media added on or after <paramref name="sinceUtc"/>.</summary>
    /// <param name="sinceUtc">The window start (UTC), usually the campaign's last send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="limit">A hard cap on the number of most-recently-added items pulled from the library.</param>
    /// <returns>The digest.</returns>
    Task<NewsletterDigest> GetNewSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken, int limit = 1000);

    /// <summary>Records the keys of a sent digest as announced by the newsletter at <paramref name="runUtc"/>.</summary>
    /// <param name="keys">The digest's <see cref="NewsletterDigest.Keys"/>.</param>
    /// <param name="runUtc">The run instant, which becomes the next window start.</param>
    void MarkAnnounced(IEnumerable<string> keys, DateTime runUtc);
}

/// <inheritdoc cref="INewsletterDigestService"/>
public sealed class NewsletterDigestService : INewsletterDigestService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigAccessor _config;
    private readonly ServerLinkContext _links;
    private readonly IAddedItemsStore _addedItems;
    private readonly IPosterCache _posterCache;
    private readonly IAnnouncedStore _announced;
    private readonly ILibraryCatalog _libraries;
    private readonly Logging.IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="NewsletterDigestService"/> class.</summary>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="links">The captured server identity for deep links.</param>
    /// <param name="addedItems">The server-time added-items store.</param>
    /// <param name="posterCache">The inline poster cache (used only when no public URL is set).</param>
    /// <param name="announced">The registry of keys already announced.</param>
    /// <param name="libraries">The library lookup behind the per-user library choice.</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public NewsletterDigestService(
        ILibraryManager libraryManager,
        IConfigAccessor config,
        ServerLinkContext links,
        IAddedItemsStore addedItems,
        IPosterCache posterCache,
        IAnnouncedStore announced,
        ILibraryCatalog libraries,
        Logging.IEasyNotifLog log)
    {
        _libraryManager = libraryManager;
        _config = config;
        _links = links;
        _addedItems = addedItems;
        _posterCache = posterCache;
        _announced = announced;
        _libraries = libraries;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<NewsletterDigest> GetNewSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken, int limit = 1000)
    {
        var baseUrl = MediaLinks.BaseUrl(_config.Get().PublicServerUrl);
        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);

        // Primary source: items the ItemAdded event recorded as added since the window start, in
        // real server time. These may have an old DateCreated (Jellyfin takes it from the file), so
        // they are fetched by id.
        var trackedIds = _addedItems.AddedSince(since);
        var tracked = trackedIds.Count == 0
            ? Array.Empty<BaseItem>()
            : _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = [.. trackedIds] });

        // Fallback: the most-recently-added items whose own DateCreated is inside the window. Covers
        // media added before the plugin started tracking. (MinDateCreated on the query itself is
        // unreliable across 10.11.x, so the window is applied in memory.)
        var byDate = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = limit,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { Fields = [] }
        });

        var candidates = tracked
            .Concat(byDate.Where(i => i.DateCreated >= since))
            .Where(i => i is Movie or Episode && !i.IsVirtualItem)
            .DistinctBy(i => i.Id)
            .ToList();

        // One entry per business key: a file re-created under a new id (replaced, renamed, moved)
        // keeps its key, so it is neither listed twice nor announced again in a later window.
        var byKey = candidates
            .GroupBy(MediaKey.For, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var alreadyAnnounced = _announced.AnnouncedOnOrBefore(AnnouncedStore.NewsletterChannel, byKey.Keys, since);
        var inLibrary = LibraryKeyLookup.PresentOutside(_libraryManager, byKey, candidates.Select(i => i.Id).ToHashSet());
        var kept = byKey
            .Where(kv => !alreadyAnnounced.Contains(kv.Key) && !inLibrary.Contains(kv.Key))
            .ToList();
        var items = kept.Select(kv => kv.Value).ToList();

        _log.Info("newsletter.query", new Dictionary<string, object?>
        {
            ["since"] = since,
            ["tracked"] = trackedIds.Count,
            ["byDate"] = byDate.Count(i => i.DateCreated >= since),
            ["duplicateKeys"] = candidates.Count - byKey.Count,
            ["alreadyAnnounced"] = alreadyAnnounced.Count,
            ["inLibrary"] = inLibrary.Count,
            ["matched"] = items.Count,
            ["hitLimit"] = byDate.Count >= limit
        });

        // Newest first, like the series, so a capped newsletter keeps the latest additions.
        var movieItems = items.OfType<Movie>().OrderByDescending(m => m.DateCreated).ToList();

        var groups = items.OfType<Episode>()
            .Where(e => e.SeriesId != Guid.Empty)
            .GroupBy(e => e.SeriesId)
            .ToList();

        var seriesItems = groups.Count == 0
            ? new Dictionary<Guid, BaseItem>()
            : _libraryManager
                .GetItemList(new InternalItemsQuery { ItemIds = groups.Select(g => g.Key).ToArray() })
                .GroupBy(i => i.Id)
                .ToDictionary(g => g.Key, g => g.First());

        // When there is no public URL, posters cannot be linked - attach a downsized copy inline.
        var inlinePosters = await MediaLinks.InlinePostersAsync(
            baseUrl, _posterCache, movieItems.Concat<BaseItem>(seriesItems.Values), cancellationToken).ConfigureAwait(false);

        var movies = movieItems
            .Select(m => new DigestMovie(
                m.Id,
                m.Name ?? string.Empty,
                m.ProductionYear,
                Trimmed(m.Overview),
                m.Genres ?? [],
                Trimmed(m.OfficialRating),
                MediaLinks.PosterUrl(baseUrl, inlinePosters, m),
                MediaLinks.DetailUrl(baseUrl, _links.SystemId, m.Id)))
            .ToList();

        var series = groups
            .Select(g =>
            {
                seriesItems.TryGetValue(g.Key, out var seriesItem);
                var seasons = g
                    .Select(e => e.ParentIndexNumber)
                    .Where(n => n is > 0)
                    .Select(n => n!.Value)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                return new
                {
                    Newest = g.Max(e => e.DateCreated),
                    Series = new DigestSeries(
                        g.Key,
                        seriesItem?.Name ?? g.First().SeriesName ?? string.Empty,
                        g.Count(),
                        seasons,
                        Trimmed(seriesItem?.Overview),
                        seriesItem is null ? null : MediaLinks.PosterUrl(baseUrl, inlinePosters, seriesItem),
                        MediaLinks.DetailUrl(baseUrl, _links.SystemId, g.Key),
                        [.. g.Select(e => new DigestEpisode(e.Id, e.ParentIndexNumber))])
                };
            })
            .OrderByDescending(x => x.Newest)
            .Select(x => x.Series)
            .ToList();

        var itemLibraries = movieItems
            .Concat<BaseItem>(groups.SelectMany(g => g))
            .ToDictionary(i => i.Id, _libraries.LibrariesOf);

        return new NewsletterDigest(movies, series, [.. inlinePosters.Values])
        {
            Keys = [.. kept.Select(kv => kv.Key)],
            ItemLibraries = itemLibraries
        };
    }

    /// <inheritdoc/>
    public void MarkAnnounced(IEnumerable<string> keys, DateTime runUtc)
        => _announced.Record(AnnouncedStore.NewsletterChannel, keys, runUtc);

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
