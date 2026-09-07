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
}

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
public sealed record DigestSeries(
    Guid Id,
    string Title,
    int NewEpisodeCount,
    IReadOnlyList<int> Seasons,
    string? Overview,
    string? PosterUrl,
    string? DetailUrl);
