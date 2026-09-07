using System.Globalization;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Templating;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Turns a <see cref="NewsletterDigest"/> into the subject, HTML body and text body of the
/// newsletter email, in the campaign's language, using the embedded newsletter template.
/// </summary>
public sealed class NewsletterComposer
{
    private readonly ITemplateStore _templates;

    /// <summary>Initializes a new instance of the <see cref="NewsletterComposer"/> class.</summary>
    /// <param name="templates">The template store.</param>
    public NewsletterComposer(ITemplateStore templates) => _templates = templates;

    /// <summary>Composes the newsletter email for a campaign and a digest.</summary>
    /// <param name="campaign">The campaign (for the mail language).</param>
    /// <param name="digest">The media digest.</param>
    /// <param name="serverName">The server's friendly name, for the heading.</param>
    /// <param name="templateId">The template id to render with (base or custom).</param>
    /// <param name="unsubscribeUrl">The recipient's one-click unsubscribe URL, or null.</param>
    /// <returns>The composed content.</returns>
    public EmailContent Compose(Campaign campaign, NewsletterDigest digest, string serverName, string templateId, string? unsubscribeUrl = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(digest);

        var fr = campaign.MailLanguage != "en";
        var n = digest.TotalItems;

        var summary = digest.IsEmpty
            ? (fr ? "Rien de neuf cette semaine" : "Nothing new this week")
            : fr ? $"{n} ajout(s)" : $"{n} addition(s)";
        var subject = fr ? $"Nouveautes mediatheque - {summary.ToLowerInvariant()}"
                         : $"New in your library - {summary.ToLowerInvariant()}";
        var heading = fr ? $"Nouveautes sur {serverName}" : $"New on {serverName}";

        var model = new Dictionary<string, object?>
        {
            ["heading"] = heading,
            ["summary"] = summary,
            ["isEmpty"] = digest.IsEmpty,
            ["hasMovies"] = digest.Movies.Count > 0,
            ["hasSeries"] = digest.Series.Count > 0,
            ["movies"] = digest.Movies.Select(m => new Dictionary<string, object?>
            {
                ["title"] = m.Title,
                ["year"] = m.Year,
                ["genres"] = string.Join(", ", m.Genres),
                ["overview"] = m.Overview,
                ["posterUrl"] = m.PosterUrl,
                ["detailUrl"] = m.DetailUrl,
                ["noLink"] = m.DetailUrl is null
            }).ToList(),
            ["series"] = digest.Series.Select(s => new Dictionary<string, object?>
            {
                ["title"] = s.Title,
                ["episodesLabel"] = EpisodesLabel(s.NewEpisodeCount, fr),
                ["seasonsLabel"] = SeasonsLabel(s.Seasons, fr),
                ["overview"] = s.Overview,
                ["posterUrl"] = s.PosterUrl,
                ["detailUrl"] = s.DetailUrl,
                ["noLink"] = s.DetailUrl is null
            }).ToList(),
            ["unsubscribeUrl"] = unsubscribeUrl
        };

        var html = TemplateEngine.Render(_templates.Get(templateId, fr ? "fr" : "en"), model);
        var attachments = digest.Posters.Count > 0 ? digest.Posters : null;
        return new EmailContent(subject, html, BuildText(digest, heading, fr, unsubscribeUrl), attachments);
    }

    private static string EpisodesLabel(int count, bool fr)
        => fr
            ? count == 1 ? "1 nouvel episode" : $"{count} nouveaux episodes"
            : count == 1 ? "1 new episode" : $"{count} new episodes";

    private static string SeasonsLabel(IReadOnlyList<int> seasons, bool fr)
    {
        if (seasons.Count == 0)
        {
            return fr ? "episodes divers" : "various episodes";
        }

        var joined = string.Join(", ", seasons.Select(s => s.ToString(CultureInfo.InvariantCulture)));
        var word = seasons.Count == 1 ? (fr ? "Saison" : "Season") : (fr ? "Saisons" : "Seasons");
        return $"{word} {joined}";
    }

    private static string BuildText(NewsletterDigest digest, string heading, bool fr, string? unsubscribeUrl)
    {
        var sb = new StringBuilder();
        sb.AppendLine(heading).AppendLine();

        if (digest.IsEmpty)
        {
            sb.AppendLine(fr ? "Rien de neuf n'a ete ajoute cette semaine." : "Nothing new was added this week.");
            AppendUnsubscribe(sb, fr, unsubscribeUrl);
            return sb.ToString();
        }

        if (digest.Movies.Count > 0)
        {
            sb.AppendLine(fr ? "Films" : "Movies");
            foreach (var m in digest.Movies)
            {
                sb.Append("- ").Append(m.Title);
                if (m.Year is { } y)
                {
                    sb.Append(" (").Append(y.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                if (m.DetailUrl is { } url)
                {
                    sb.Append("  ").Append(url);
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        if (digest.Series.Count > 0)
        {
            sb.AppendLine(fr ? "Series" : "Series");
            foreach (var s in digest.Series)
            {
                sb.Append("- ").Append(s.Title).Append(" : ").Append(EpisodesLabel(s.NewEpisodeCount, fr));
                if (s.DetailUrl is { } url)
                {
                    sb.Append("  ").Append(url);
                }

                sb.AppendLine();
            }
        }

        AppendUnsubscribe(sb, fr, unsubscribeUrl);
        return sb.ToString();
    }

    private static void AppendUnsubscribe(StringBuilder sb, bool fr, string? unsubscribeUrl)
    {
        if (unsubscribeUrl is null)
        {
            return;
        }

        sb.AppendLine()
          .Append(fr ? "Se desabonner : " : "Unsubscribe: ")
          .AppendLine(unsubscribeUrl);
    }
}
