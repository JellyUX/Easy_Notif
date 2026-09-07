namespace Jellyfin.Plugin.EasyNotif.Recap;

/// <summary>The shape of one line in the "what you watched this week" list.</summary>
public enum RecapKind
{
    /// <summary>A single movie.</summary>
    Movie,

    /// <summary>A single episode of a series (only one episode of that series was watched).</summary>
    Episode,

    /// <summary>A group of two or more episodes of the same series.</summary>
    Series
}

/// <summary>One entry in a user's weekly watch list.</summary>
/// <param name="Kind">Whether this is a movie, a single episode, or a grouped series.</param>
/// <param name="Title">The movie title, the episode title, or the series title (for a group).</param>
/// <param name="SeriesTitle">The parent series title for a single <see cref="RecapKind.Episode"/>; null otherwise.</param>
/// <param name="EpisodeCount">The number of episodes for a <see cref="RecapKind.Series"/> group; 1 otherwise.</param>
/// <param name="WhenUtc">The most recent watch time in the entry (UTC).</param>
/// <param name="Completed">Whether at least one view in the entry counted as completed.</param>
public sealed record WatchedEntry(
    RecapKind Kind,
    string Title,
    string? SeriesTitle,
    int EpisodeCount,
    DateTime WhenUtc,
    bool Completed);

/// <summary>The data for one user's weekly recap email.</summary>
/// <param name="Watched">This week's entries, newest first.</param>
/// <param name="YearTotal">Significant views so far this year.</param>
/// <param name="YearCompleted">Views that counted as completed so far this year.</param>
/// <param name="PartialSince">When Easy Notif started tracking, when that is later than the first of
/// the recap year (the annual total is not a full year yet); null once a full year is covered.</param>
/// <param name="UserName">The user's Jellyfin username for the salutation; null when unknown.</param>
public sealed record RecapModel(
    IReadOnlyList<WatchedEntry> Watched,
    int YearTotal,
    int YearCompleted,
    DateTime? PartialSince,
    string? UserName)
{
    /// <summary>Gets a value indicating whether the user watched nothing significant this week.</summary>
    public bool QuietWeek => Watched.Count == 0;

    /// <summary>Gets the number of titles watched this week (episodes counted individually).</summary>
    public int WeekTitles => Watched.Sum(w => w.Kind == RecapKind.Series ? w.EpisodeCount : 1);
}
