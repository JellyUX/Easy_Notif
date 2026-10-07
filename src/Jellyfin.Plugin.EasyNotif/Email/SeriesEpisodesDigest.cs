using System.Globalization;
using System.Net;
using System.Text;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>One new episode in a series notification.</summary>
/// <param name="Season">The season number (0 for specials), or null.</param>
/// <param name="Number">The episode number, or null.</param>
/// <param name="NumberEnd">The last episode number of a multi-episode file, or null.</param>
/// <param name="Premiere">The premiere date, for dated episodes without a number.</param>
public sealed record SeriesEpisodeLine(int? Season, int? Number, int? NumberEnd, DateTime? Premiere);

/// <summary>The new episodes of one followed series.</summary>
/// <param name="SeriesId">The series id.</param>
/// <param name="SeriesName">The series name.</param>
/// <param name="Episodes">The episodes, in season and episode order.</param>
public sealed record SeriesEpisodesGroup(Guid SeriesId, string SeriesName, IReadOnlyList<SeriesEpisodeLine> Episodes);

/// <summary>The content of one series notification email: the new episodes of each followed series.</summary>
/// <param name="Series">The series, in name order.</param>
public sealed record SeriesEpisodesDigest(IReadOnlyList<SeriesEpisodesGroup> Series);

/// <summary>
/// Provisional rendering of a <see cref="SeriesEpisodesDigest"/> (DEC-33): plain HTML and text built
/// in code, French or English. The embedded <c>series-episodes</c> template replaces it in phase 13.
/// </summary>
public static class SeriesEpisodesRenderer
{
    /// <summary>Above this many episodes, a series is summarised in one line.</summary>
    public const int SummaryThreshold = 5;

    /// <summary>Renders the digest.</summary>
    /// <param name="digest">The digest.</param>
    /// <param name="french">True for French, false for English.</param>
    /// <returns>The subject, HTML body and text body.</returns>
    public static (string Subject, string Html, string Text) Render(SeriesEpisodesDigest digest, bool french)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var subject = digest.Series.Count == 1
            ? (french ? "Nouveaux épisodes : " : "New episodes: ") + digest.Series[0].SeriesName
            : french
                ? string.Create(CultureInfo.InvariantCulture, $"Nouveaux épisodes de {digest.Series.Count} séries")
                : string.Create(CultureInfo.InvariantCulture, $"New episodes of {digest.Series.Count} series");

        var html = new StringBuilder();
        var text = new StringBuilder();
        foreach (var group in digest.Series)
        {
            html.Append("<h2>").Append(WebUtility.HtmlEncode(group.SeriesName)).Append("</h2><ul>");
            text.Append(group.SeriesName).Append('\n');
            foreach (var line in Lines(group, french))
            {
                html.Append("<li>").Append(WebUtility.HtmlEncode(line)).Append("</li>");
                text.Append("- ").Append(line).Append('\n');
            }

            html.Append("</ul>");
            text.Append('\n');
        }

        return (subject, html.ToString(), text.ToString().TrimEnd() + "\n");
    }

    /// <summary>Returns the label of one episode.</summary>
    /// <param name="line">The episode.</param>
    /// <param name="french">True for French, false for English.</param>
    /// <returns><c>S02E05</c>, <c>S02E05-E06</c>, <c>Spécial 3</c>, a premiere date, or <c>Nouvel épisode</c>.</returns>
    public static string Label(SeriesEpisodeLine line, bool french)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Number is { } number)
        {
            if (line.Season == 0)
            {
                return (french ? "Spécial " : "Special ") + number.ToString(CultureInfo.InvariantCulture);
            }

            var label = line.Season is { } season
                ? string.Create(CultureInfo.InvariantCulture, $"S{season:00}E{number:00}")
                : string.Create(CultureInfo.InvariantCulture, $"E{number:00}");
            return line.NumberEnd is { } end && end > number
                ? string.Create(CultureInfo.InvariantCulture, $"{label}-E{end:00}")
                : label;
        }

        if (line.Premiere is { } premiere)
        {
            return premiere.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return french ? "Nouvel épisode" : "New episode";
    }

    private static IEnumerable<string> Lines(SeriesEpisodesGroup group, bool french)
    {
        if (group.Episodes.Count <= SummaryThreshold)
        {
            return group.Episodes.Select(e => Label(e, french));
        }

        var first = Label(group.Episodes[0], french);
        var last = Label(group.Episodes[^1], french);
        var count = group.Episodes.Count.ToString(CultureInfo.InvariantCulture);
        return [french ? $"{count} nouveaux épisodes ({first} à {last})" : $"{count} new episodes ({first} to {last})"];
    }
}
