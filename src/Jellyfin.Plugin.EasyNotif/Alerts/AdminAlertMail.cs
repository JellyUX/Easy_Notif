using System.Globalization;
using System.Net;
using System.Text;

namespace Jellyfin.Plugin.EasyNotif.Alerts;

/// <summary>
/// Builds the daily incident digest sent to the administrator (FEAT-04, DEC-65): one line per
/// incident type with its count, first and last time, and the latest detail. Rendered in code like
/// <see cref="Email.TestEmail"/>, outside the template system; plain markup, no colours or fonts.
/// </summary>
public static class AdminAlertMail
{
    private static readonly Dictionary<string, (string Fr, string En)> Labels = new(StringComparer.Ordinal)
    {
        ["email.failed"] = ("Envoi refusé ou en échec", "Send refused or failed"),
        ["quota.threshold"] = ("80 % d'une limite d'envoi atteints", "80 percent of a sending limit reached"),
        ["quota.suspended"] = ("Envois suspendus pour quota", "Sending paused for quota"),
        ["dispatch.error"] = ("Campagne en erreur", "Campaign error"),
        ["dispatch.retry-abandoned"] = ("Rejeu de campagne abandonné", "Campaign retry abandoned"),
        ["series.retry-abandoned"] = ("Rejeu de mail de séries abandonné", "Series email retry abandoned"),
        ["announcement.retry-abandoned"] = ("Rejeu d'annonce programmée abandonné", "Scheduled announcement retry abandoned"),
        ["webhook.rejected"] = ("Adresse rejetée (bounce)", "Address rejected (bounce)"),
        ["webhook.complaint"] = ("Plainte pour spam", "Spam complaint"),
        ["drain.lost"] = ("Lot perdu à l'écriture", "Batch lost on write"),
        ["drain.dropped"] = ("Éléments écartés (file pleine)", "Items dropped (queue full)")
    };

    /// <summary>Builds the digest subject and HTML body.</summary>
    /// <param name="lang">The language, <c>"fr"</c> or anything else for English.</param>
    /// <param name="incidents">The incidents to report, any order.</param>
    /// <returns>The subject and HTML body.</returns>
    public static (string Subject, string Html) Build(string? lang, IReadOnlyCollection<Incident> incidents)
    {
        ArgumentNullException.ThrowIfNull(incidents);
        var french = string.Equals(lang, "fr", StringComparison.OrdinalIgnoreCase);

        var html = new StringBuilder();
        html.Append(french
            ? "<p>Easy Notif a relevé des incidents depuis le dernier résumé. Le détail est dans l'onglet Statut de la page du plugin et dans son journal.</p>"
            : "<p>Easy Notif recorded incidents since the last digest. The details are in the Status tab of the plugin page and in its log.</p>");
        html.Append("<table><thead><tr><th>")
            .Append(french ? "Incident" : "Incident").Append("</th><th>")
            .Append(french ? "Nombre" : "Count").Append("</th><th>")
            .Append(french ? "Premier (UTC)" : "First (UTC)").Append("</th><th>")
            .Append(french ? "Dernier (UTC)" : "Last (UTC)").Append("</th><th>")
            .Append(french ? "Dernier détail" : "Latest detail").Append("</th></tr></thead><tbody>");

        foreach (var group in incidents.GroupBy(i => i.Type).OrderBy(g => g.Min(i => i.Ts)))
        {
            var ordered = group.OrderBy(i => i.Ts).ToList();
            // Labels are constants; an unknown type (none today) is encoded like the detail.
            var label = Labels.TryGetValue(group.Key, out var l) ? (french ? l.Fr : l.En) : WebUtility.HtmlEncode(group.Key);
            html.Append("<tr><td>").Append(label)
                .Append("</td><td>").Append(ordered.Count.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(Format(ordered[0].Ts))
                .Append("</td><td>").Append(Format(ordered[^1].Ts))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(ordered[^1].Detail ?? "-"))
                .Append("</td></tr>");
        }

        html.Append("</tbody></table>");
        html.Append(french
            ? "<p>Au plus un résumé par jour. Pour ne plus le recevoir, videz l'adresse d'alerte dans les réglages du plugin.</p>"
            : "<p>At most one digest a day. To stop it, clear the alert address in the plugin settings.</p>");

        var count = incidents.Count.ToString(CultureInfo.InvariantCulture);
        var subject = french ? $"Easy Notif - {count} incident(s) à vérifier" : $"Easy Notif - {count} incident(s) to check";
        return (subject, html.ToString());
    }

    private static string Format(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
