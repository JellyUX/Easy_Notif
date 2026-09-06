using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.EasyNotif.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    /// <param name="limit">A hard cap on the number of most-recently-added items pulled from the library.</param>
    /// <returns>The digest.</returns>
    NewsletterDigest GetNewSince(DateTime sinceUtc, int limit = 1000);
}

/// <inheritdoc cref="INewsletterDigestService"/>
public sealed class NewsletterDigestService : INewsletterDigestService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigAccessor _config;
    private readonly ServerLinkContext _links;
    private readonly IAddedItemsStore _addedItems;
    private readonly Logging.IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="NewsletterDigestService"/> class.</summary>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="links">The captured server identity for deep links.</param>
    /// <param name="addedItems">The server-time added-items store.</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public NewsletterDigestService(
        ILibraryManager libraryManager,
        IConfigAccessor config,
        ServerLinkContext links,
        IAddedItemsStore addedItems,
        Logging.IEasyNotifLog log)
    {
        _libraryManager = libraryManager;
        _config = config;
        _links = links;
        _addedItems = addedItems;
        _log = log;
    }

    /// <inheritdoc/>
    public NewsletterDigest GetNewSince(DateTime sinceUtc, int limit = 1000)
    {
        var baseUrl = _config.Get().PublicServerUrl?.TrimEnd('/');
        var hasPublicUrl = !string.IsNullOrWhiteSpace(baseUrl);
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

        var items = tracked
            .Concat(byDate.Where(i => i.DateCreated >= since))
            .Where(i => i is Movie or Episode && !i.IsVirtualItem)
            .DistinctBy(i => i.Id)
            .ToList();

        _log.Info("newsletter.query", new Dictionary<string, object?>
        {
            ["since"] = since,
            ["tracked"] = trackedIds.Count,
            ["byDate"] = byDate.Count(i => i.DateCreated >= since),
            ["matched"] = items.Count,
            ["hitLimit"] = byDate.Count >= limit
        });

        var movies = items.OfType<Movie>()
            .Select(m => new DigestMovie(
                m.Id,
                m.Name ?? string.Empty,
                m.ProductionYear,
                Trimmed(m.Overview),
                m.Genres ?? [],
                Trimmed(m.OfficialRating),
                PosterUrl(baseUrl, hasPublicUrl, m),
                DetailUrl(baseUrl, hasPublicUrl, m.Id)))
            .ToList();

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
                        seriesItem is null ? null : PosterUrl(baseUrl, hasPublicUrl, seriesItem),
                        DetailUrl(baseUrl, hasPublicUrl, g.Key))
                };
            })
            .OrderByDescending(x => x.Newest)
            .Select(x => x.Series)
            .ToList();

        return new NewsletterDigest(movies, series);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? PosterUrl(string? baseUrl, bool hasPublicUrl, BaseItem item)
        => hasPublicUrl && item.HasImage(ImageType.Primary)
            ? $"{baseUrl}/Items/{item.Id:N}/Images/Primary?maxWidth=300&quality=85"
            : null;

    private string? DetailUrl(string? baseUrl, bool hasPublicUrl, Guid id)
        => hasPublicUrl
            ? $"{baseUrl}/web/#/details?id={id:N}&serverId={_links.SystemId}"
            : null;
}
