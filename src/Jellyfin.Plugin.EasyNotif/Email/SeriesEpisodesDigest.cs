using System.Globalization;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Templating;

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
/// <param name="UnsubscribeUrl">The link that stops this series only, or null without a public URL or secret.</param>
/// <param name="PosterUrl">The poster: an image URL, a <c>cid:</c> reference, or null.</param>
/// <param name="DetailUrl">The series page in the web client, or null without a public URL.</param>
public sealed record SeriesEpisodesGroup(
    Guid SeriesId,
    string SeriesName,
    IReadOnlyList<SeriesEpisodeLine> Episodes,
    string? UnsubscribeUrl = null,
    string? PosterUrl = null,
    string? DetailUrl = null);

/// <summary>The content of one series notification email: the new episodes of each followed series.</summary>
/// <param name="Series">The series, in name order.</param>
/// <param name="UnsubscribeAllUrl">The link that stops every new-episode email, or null without a public URL or secret.</param>
/// <param name="Posters">The inline posters the <c>cid:</c> references point to, or null.</param>
public sealed record SeriesEpisodesDigest(
    IReadOnlyList<SeriesEpisodesGroup> Series,
    string? UnsubscribeAllUrl = null,
    IReadOnlyList<EmailAttachment>? Posters = null);

/// <summary>
/// Renders a <see cref="SeriesEpisodesDigest"/>: the HTML body through the embedded
/// <c>series-episodes</c> template, the subject and the text body in code, French or English.
/// </summary>
public static class SeriesEpisodesRenderer
{
    /// <summary>The base template id of the series notification email.</summary>
    public const string TemplateId = "series-episodes";

    /// <summary>Above this many episodes, a series is summarised in one line.</summary>
    public const int SummaryThreshold = 5;

    /// <summary>Renders the digest.</summary>
    /// <param name="digest">The digest.</param>
    /// <param name="french">True for French, false for English.</param>
    /// <param name="template">The <c>series-episodes</c> template text in the same language.</param>
    /// <param name="serverName">The server name, for the heading.</param>
    /// <returns>The subject, HTML body and text body.</returns>
    public static (string Subject, string Html, string Text) Render(SeriesEpisodesDigest digest, bool french, string template, string serverName)
    {
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(template);
        var subject = digest.Series.Count == 1
            ? (french ? "Nouveaux épisodes : " : "New episodes: ") + digest.Series[0].SeriesName
            : french
                ? string.Create(CultureInfo.InvariantCulture, $"Nouveaux épisodes de {digest.Series.Count} séries")
                : string.Create(CultureInfo.InvariantCulture, $"New episodes of {digest.Series.Count} series");
        var html = TemplateEngine.Render(template, Model(digest, french, serverName));
        return (subject, html, Text(digest, french));
    }

    /// <summary>Returns the template model of a digest.</summary>
    /// <param name="digest">The digest.</param>
    /// <param name="french">True for French, false for English.</param>
    /// <param name="serverName">The server name, for the heading.</param>
    /// <returns>The model.</returns>
    public static IReadOnlyDictionary<string, object?> Model(SeriesEpisodesDigest digest, bool french, string serverName)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var total = digest.Series.Sum(s => s.Episodes.Count);
        var count = total.ToString(CultureInfo.InvariantCulture);
        return new Dictionary<string, object?>
        {
            ["heading"] = french ? $"Nouveaux épisodes sur {serverName}" : $"New episodes on {serverName}",
            ["summary"] = total == 1
                ? (french ? "1 nouvel épisode" : "1 new episode")
                : french ? $"{count} nouveaux épisodes" : $"{count} new episodes",
            ["series"] = digest.Series.Select(s => new Dictionary<string, object?>
            {
                ["title"] = s.SeriesName,
                ["posterUrl"] = s.PosterUrl,
                ["detailUrl"] = s.DetailUrl,
                ["noLink"] = s.DetailUrl is null,
                ["episodes"] = Lines(s, french).ToList(),
                ["stopUrl"] = s.UnsubscribeUrl
            }).ToList(),
            ["unsubscribeUrl"] = digest.UnsubscribeAllUrl
        };
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

    private static string Text(SeriesEpisodesDigest digest, bool french)
    {
        var text = new StringBuilder();
        foreach (var group in digest.Series)
        {
            text.Append(group.SeriesName).Append('\n');
            foreach (var line in Lines(group, french))
            {
                text.Append("- ").Append(line).Append('\n');
            }

            if (group.DetailUrl is { } detailUrl)
            {
                text.Append(french ? "Voir la série" : "Open the series").Append(": ").Append(detailUrl).Append('\n');
            }

            if (group.UnsubscribeUrl is { } seriesUrl)
            {
                text.Append(french ? "Ne plus suivre cette série" : "Stop following this series").Append(": ").Append(seriesUrl).Append('\n');
            }

            text.Append('\n');
        }

        if (digest.UnsubscribeAllUrl is { } allUrl)
        {
            text.Append(french ? "Ne plus recevoir les mails de nouveaux épisodes" : "Stop all new-episode emails").Append(": ").Append(allUrl).Append('\n');
        }

        return text.ToString().TrimEnd() + "\n";
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
