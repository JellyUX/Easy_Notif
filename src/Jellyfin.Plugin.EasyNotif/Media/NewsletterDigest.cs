using Jellyfin.Plugin.EasyNotif.Email;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// The media added to the library within a newsletter window: standalone movies, plus episodes
/// grouped by their series (Synthese.md section 2.3).
/// </summary>
/// <param name="Movies">The new movies, newest first.</param>
/// <param name="Series">The series with new episodes, newest first.</param>
/// <param name="InlinePosters">Poster images to attach as inline <c>cid:</c> parts, used only when
/// no public server URL is configured; empty otherwise.</param>
public sealed record NewsletterDigest(
    IReadOnlyList<DigestMovie> Movies,
    IReadOnlyList<DigestSeries> Series,
    IReadOnlyList<EmailAttachment>? InlinePosters = null)
{
    /// <summary>Gets a value indicating whether nothing new was added in the window.</summary>
    public bool IsEmpty => Movies.Count == 0 && Series.Count == 0;

    /// <summary>Gets the count of distinct additions (movies plus new episodes).</summary>
    public int TotalItems => Movies.Count + Series.Sum(s => s.NewEpisodeCount);

    /// <summary>Gets the inline poster attachments (never null).</summary>
    public IReadOnlyList<EmailAttachment> Posters => InlinePosters ?? [];

    /// <summary>Gets the ids an access check must cover: every movie and every new episode.</summary>
    public IReadOnlyList<Guid> ItemIds
        => [.. Movies.Select(m => m.Id), .. Series.SelectMany(s => s.EpisodeList.Select(e => e.Id))];

    /// <summary>
    /// Narrows the digest to the items a recipient may see: forbidden movies are dropped, each series
    /// is recounted from its visible episodes (and dropped when none is left), and only the inline
    /// posters still referenced are kept.
    /// </summary>
    /// <param name="visible">The visible item ids, from <see cref="IAccessFilter"/>.</param>
    /// <returns>The narrowed digest.</returns>
    public NewsletterDigest For(IReadOnlySet<Guid> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        var movies = Movies.Where(m => visible.Contains(m.Id)).ToList();
        var series = Series
            .Select(s => s with { Episodes = [.. s.EpisodeList.Where(e => visible.Contains(e.Id))] })
            .Where(s => s.EpisodeList.Count > 0)
            .Select(s => s with
            {
                NewEpisodeCount = s.EpisodeList.Count,
                Seasons = [.. s.EpisodeList.Where(e => e.Season is > 0).Select(e => e.Season!.Value).Distinct().Order()]
            })
            .ToList();
        var referenced = movies.Select(m => m.PosterUrl).Concat(series.Select(s => s.PosterUrl)).ToHashSet();
        var posters = Posters.Where(p => referenced.Contains($"cid:{p.ContentId}")).ToList();
        return new NewsletterDigest(movies, series, posters);
    }
}

/// <summary>One newly added episode of a <see cref="DigestSeries"/>, kept for per-recipient filtering.</summary>
/// <param name="Id">The episode item id.</param>
/// <param name="Season">The season number, if known.</param>
public sealed record DigestEpisode(Guid Id, int? Season);

/// <summary>One newly added movie.</summary>
/// <param name="Id">The Jellyfin item id.</param>
/// <param name="Title">The movie title.</param>
/// <param name="Year">The production year, if known.</param>
/// <param name="Overview">The synopsis, if any.</param>
/// <param name="Genres">The genres.</param>
/// <param name="OfficialRating">The age rating, if any.</param>
/// <param name="PosterUrl">The absolute poster URL, or null when no public server URL / no image.</param>
/// <param name="DetailUrl">The absolute web deep link, or null when no public server URL.</param>
public sealed record DigestMovie(
    Guid Id,
    string Title,
    int? Year,
    string? Overview,
    IReadOnlyList<string> Genres,
    string? OfficialRating,
    string? PosterUrl,
    string? DetailUrl);

/// <summary>One series with one or more newly added episodes in the window.</summary>
/// <param name="Id">The series item id.</param>
/// <param name="Title">The series title.</param>
/// <param name="NewEpisodeCount">How many new episodes were added.</param>
/// <param name="Seasons">The distinct season numbers touched, ascending.</param>
/// <param name="Overview">The series synopsis, if any.</param>
/// <param name="PosterUrl">The absolute poster URL, or null when no public server URL / no image.</param>
/// <param name="DetailUrl">The absolute web deep link, or null when no public server URL.</param>
/// <param name="Episodes">The new episodes behind <paramref name="NewEpisodeCount"/>; null when
/// unknown, in which case <see cref="NewsletterDigest.For"/> treats the series as having none.</param>
public sealed record DigestSeries(
    Guid Id,
    string Title,
    int NewEpisodeCount,
    IReadOnlyList<int> Seasons,
    string? Overview,
    string? PosterUrl,
    string? DetailUrl,
    IReadOnlyList<DigestEpisode>? Episodes = null)
{
    /// <summary>Gets the new episodes (never null).</summary>
    public IReadOnlyList<DigestEpisode> EpisodeList => Episodes ?? [];
}
