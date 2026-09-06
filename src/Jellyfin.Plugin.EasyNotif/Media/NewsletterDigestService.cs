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
    /// <param name="limit">A hard cap on the number of items pulled from the library.</param>
    /// <returns>The digest.</returns>
    NewsletterDigest GetNewSince(DateTime sinceUtc, int limit = 500);
}

/// <inheritdoc cref="INewsletterDigestService"/>
public sealed class NewsletterDigestService : INewsletterDigestService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IConfigAccessor _config;
    private readonly ServerLinkContext _links;

    /// <summary>Initializes a new instance of the <see cref="NewsletterDigestService"/> class.</summary>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="links">The captured server identity for deep links.</param>
    public NewsletterDigestService(ILibraryManager libraryManager, IConfigAccessor config, ServerLinkContext links)
    {
        _libraryManager = libraryManager;
        _config = config;
        _links = links;
    }

    /// <inheritdoc/>
    public NewsletterDigest GetNewSince(DateTime sinceUtc, int limit = 500)
    {
        var baseUrl = _config.Get().PublicServerUrl?.TrimEnd('/');
        var hasPublicUrl = !string.IsNullOrWhiteSpace(baseUrl);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MinDateCreated = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc),
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            Limit = limit,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)]
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
