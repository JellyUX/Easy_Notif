using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// The business key of an announceable item: stable when Jellyfin re-creates the item under a new id
/// (a replaced, renamed or moved file). Computed on the item as read when the digest is built, never at
/// <c>ItemAdded</c> time, when the series, season and episode numbers are still empty.
/// </summary>
public static class MediaKey
{
    /// <summary>Returns the key of an episode or a movie.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>
    /// Episode: <c>ep:{series}:{season}:{episode}[-{end}]</c>, else <c>ep:{series}:{premiere date}</c>,
    /// else <c>id:{id}</c>. Movie: <c>movie:tmdb:{id}</c>, else <c>movie:imdb:{id}</c>, else <c>id:{id}</c>.
    /// </returns>
    public static string For(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            Episode e => ForEpisode(e),
            Movie m => ForMovie(m),
            _ => ById(item.Id)
        };
    }

    private static string ForEpisode(Episode e)
    {
        if (e.SeriesId == Guid.Empty)
        {
            return ById(e.Id);
        }

        if (e.ParentIndexNumber is { } season && e.IndexNumber is { } number)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"ep:{e.SeriesId:N}:{season}:{number}");
            return e.IndexNumberEnd is { } end && end > number
                ? string.Create(CultureInfo.InvariantCulture, $"{key}-{end}")
                : key;
        }

        return e.PremiereDate is { } premiere
            ? string.Create(CultureInfo.InvariantCulture, $"ep:{e.SeriesId:N}:{premiere:yyyy-MM-dd}")
            : ById(e.Id);
    }

    private static string ForMovie(Movie m)
    {
        if (m.TryGetProviderId(MetadataProvider.Tmdb, out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
        {
            return "movie:tmdb:" + tmdb.Trim();
        }

        if (m.TryGetProviderId(MetadataProvider.Imdb, out var imdb) && !string.IsNullOrWhiteSpace(imdb))
        {
            return "movie:imdb:" + imdb.Trim();
        }

        return ById(m.Id);
    }

    private static string ById(Guid id) => $"id:{id:N}";
}
