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

    /// <summary>Gets the count of distinct additions (movies plus new episodes), those cut by <see cref="Capped"/> included.</summary>
    public int TotalItems => Movies.Count + Series.Sum(s => s.NewEpisodeCount) + HiddenItems;

    /// <summary>Gets how many movies <see cref="Capped"/> left out of <see cref="Movies"/>.</summary>
    public int MoreMovies { get; init; }

    /// <summary>Gets how many series <see cref="Capped"/> left out of <see cref="Series"/>.</summary>
    public int MoreSeries { get; init; }

    /// <summary>Gets how many movies and new episodes the left-out entries stand for.</summary>
    public int HiddenItems { get; init; }

    /// <summary>Gets the inline poster attachments (never null).</summary>
    public IReadOnlyList<EmailAttachment> Posters => InlinePosters ?? [];

    /// <summary>
    /// Gets the business keys (<see cref="MediaKey"/>) of every movie and episode in the digest, recorded
    /// as announced once the run is sent. Not carried over by <see cref="For"/>.
    /// </summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>Gets the ids an access check must cover: every movie and every new episode.</summary>
    public IReadOnlyList<Guid> ItemIds
        => [.. Movies.Select(m => m.Id), .. Series.SelectMany(s => s.EpisodeList.Select(e => e.Id))];

    /// <summary>
    /// Gets the libraries of each movie and new episode, by item id, for the per-user library choice
    /// (FEAT-02). An item missing here, or with no library, is never excluded. Not carried over by
    /// <see cref="For"/>.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> ItemLibraries { get; init; } = new Dictionary<Guid, IReadOnlyList<Guid>>();

    /// <summary>
    /// Removes from <paramref name="visible"/> the items whose libraries are all in
    /// <paramref name="excludedLibraries"/>. An item in at least one kept library stays; an
    /// excluded id that matches no library is ignored (DEC-74).
    /// </summary>
    /// <param name="visible">The visible item ids, from <see cref="IAccessFilter"/>.</param>
    /// <param name="excludedLibraries">The recipient's excluded libraries; null or empty keeps everything.</param>
    /// <returns>The ids left.</returns>
    public IReadOnlySet<Guid> InChosenLibraries(IReadOnlySet<Guid> visible, IReadOnlyCollection<Guid>? excludedLibraries)
    {
        ArgumentNullException.ThrowIfNull(visible);
        if (excludedLibraries is null || excludedLibraries.Count == 0)
        {
            return visible;
        }

        return visible
            .Where(id => !ItemLibraries.TryGetValue(id, out var libraries)
                || libraries.Count == 0
                || libraries.Any(l => !excludedLibraries.Contains(l)))
            .ToHashSet();
    }

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
        return new NewsletterDigest(movies, series, PostersFor(movies, series));
    }

    /// <summary>
    /// Keeps the first <paramref name="maxMovies"/> movies and <paramref name="maxSeries"/> series (both
    /// newest first) and counts the rest, so a mass import still yields a mail of bounded size. Applied
    /// after <see cref="For"/>, so a recipient's cap is filled with items they may see.
    /// </summary>
    /// <param name="maxMovies">The movie cap (at least 1).</param>
    /// <param name="maxSeries">The series cap (at least 1).</param>
    /// <returns>The capped digest.</returns>
    public NewsletterDigest Capped(int maxMovies, int maxSeries)
    {
        maxMovies = Math.Max(1, maxMovies);
        maxSeries = Math.Max(1, maxSeries);
        if (Movies.Count <= maxMovies && Series.Count <= maxSeries)
        {
            return this;
        }

        var movies = Movies.Take(maxMovies).ToList();
        var series = Series.Take(maxSeries).ToList();
        var hiddenSeries = Series.Skip(maxSeries).ToList();
        return new NewsletterDigest(movies, series, PostersFor(movies, series))
        {
            Keys = Keys,
            MoreMovies = Movies.Count - movies.Count,
            MoreSeries = hiddenSeries.Count,
            HiddenItems = Movies.Count - movies.Count + hiddenSeries.Sum(s => s.NewEpisodeCount)
        };
    }

    private List<EmailAttachment> PostersFor(IEnumerable<DigestMovie> movies, IEnumerable<DigestSeries> series)
    {
        var referenced = movies.Select(m => m.PosterUrl).Concat(series.Select(s => s.PosterUrl)).ToHashSet();
        return Posters.Where(p => referenced.Contains($"cid:{p.ContentId}")).ToList();
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
