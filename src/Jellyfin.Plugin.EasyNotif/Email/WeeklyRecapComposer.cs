using System.Globalization;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Recap;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Templating;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Turns a per-user <see cref="RecapModel"/> into the subject, HTML body and text body of the
/// weekly watch recap email, in the campaign's language, using the embedded recap template.
/// </summary>
public sealed class WeeklyRecapComposer
{
    private readonly ITemplateStore _templates;

    /// <summary>Initializes a new instance of the <see cref="WeeklyRecapComposer"/> class.</summary>
    /// <param name="templates">The template store.</param>
    public WeeklyRecapComposer(ITemplateStore templates) => _templates = templates;

    /// <summary>Composes the weekly recap email for a campaign and one user's data.</summary>
    /// <param name="campaign">The campaign (for the mail language).</param>
    /// <param name="model">The user's recap data.</param>
    /// <param name="serverName">The server's friendly name, for the heading.</param>
    /// <param name="templateId">The template id to render with (base or custom).</param>
    /// <returns>The composed content.</returns>
    public EmailContent Compose(Campaign campaign, RecapModel model, string serverName, string templateId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(model);

        var fr = campaign.MailLanguage != "en";

        var greeting = model.UserName is { Length: > 0 } name
            ? (fr ? $"Bonjour {name}" : $"Hi {name}")
            : (fr ? "Bonjour" : "Hi");

        var heading = fr ? $"Ta semaine sur {serverName}" : $"Your week on {serverName}";

        var subject = model.QuietWeek
            ? (fr ? "Ton resume de la semaine (semaine calme)" : "Your week in review (a quiet week)")
            : fr
                ? $"Ton resume de la semaine - {model.WeekTitles} titre(s)"
                : $"Your week in review - {model.WeekTitles} title(s)";

        var partialSince = model.PartialSince?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var templateModel = new Dictionary<string, object?>
        {
            ["greeting"] = greeting,
            ["heading"] = heading,
            ["quietWeek"] = model.QuietWeek,
            ["hasWatched"] = model.Watched.Count > 0,
            ["watched"] = model.Watched.Select(w => new Dictionary<string, object?>
            {
                ["title"] = w.Title,
                ["seriesTitle"] = w.SeriesTitle,
                ["isSeries"] = w.Kind == RecapKind.Series,
                ["isSingle"] = w.Kind != RecapKind.Series,
                ["episodeCount"] = w.EpisodeCount,
                ["when"] = w.WhenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["completed"] = w.Completed
            }).ToList(),
            ["yearTotal"] = model.YearTotal,
            ["yearCompleted"] = model.YearCompleted,
            ["partialSince"] = partialSince
        };

        var html = TemplateEngine.Render(_templates.Get(templateId, fr ? "fr" : "en"), templateModel);
        return new EmailContent(subject, html, BuildText(model, greeting, partialSince, fr));
    }

    private static string BuildText(RecapModel model, string greeting, string? partialSince, bool fr)
    {
        var sb = new StringBuilder();
        sb.AppendLine(greeting).AppendLine();

        if (model.QuietWeek)
        {
            sb.AppendLine(fr
                ? "Rien vu cette semaine. On se retrouve la semaine prochaine."
                : "Nothing watched this week. See you next week.");
        }
        else
        {
            sb.AppendLine(fr ? "Cette semaine :" : "This week:");
            foreach (var entry in model.Watched)
            {
                sb.Append("- ");
                if (entry.Kind == RecapKind.Series)
                {
                    sb.Append(entry.Title)
                      .Append(fr ? $" - {entry.EpisodeCount} episodes" : $" - {entry.EpisodeCount} episodes");
                }
                else if (entry.Kind == RecapKind.Episode && entry.SeriesTitle is { Length: > 0 } series)
                {
                    sb.Append(series).Append(" - ").Append(entry.Title);
                }
                else
                {
                    sb.Append(entry.Title);
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine(fr
            ? $"{model.YearCompleted} titre(s) termine(s) cette annee ({model.YearTotal} au total)."
            : $"{model.YearCompleted} title(s) finished this year ({model.YearTotal} in total).");

        if (partialSince is not null)
        {
            sb.AppendLine(fr
                ? $"Total tenu depuis l'installation d'Easy Notif le {partialSince}."
                : $"Counted since Easy Notif was installed on {partialSince}.");
        }

        return sb.ToString();
    }
}
