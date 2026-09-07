using System.Text;

namespace Jellyfin.Plugin.EasyNotif.Unsubscribe;

/// <summary>
/// Builds the small self-contained confirmation page served at <c>GET|POST /EasyNotif/u/{token}</c>
/// (Synthese.md section 7.2). Neutral theme via CSS system colours (adapts to the mail client's
/// light or dark rendering), no external assets, no forbidden fonts or colours (R11).
/// </summary>
public static class UnsubscribePage
{
    /// <summary>Builds the page.</summary>
    /// <param name="lang">The language, <c>fr</c> or <c>en</c>.</param>
    /// <param name="category">The category the token targets: <c>news</c>, <c>recap</c> or <c>all</c>.</param>
    /// <param name="subscribed">Whether the user is currently subscribed to that category.</param>
    /// <param name="done">True after a POST, to show a confirmation line.</param>
    /// <returns>A complete HTML document.</returns>
    public static string Build(string lang, string category, bool subscribed, bool done)
    {
        var fr = lang == "fr";
        var label = CategoryLabel(category, fr);

        var title = fr ? "Préférences d'email Easy Notif" : "Easy Notif email preferences";

        string state = (done, subscribed) switch
        {
            (true, false) => fr
                ? $"Vous ne recevrez plus les emails : {label}."
                : $"You will no longer receive these emails: {label}.",
            (true, true) => fr
                ? $"Vous recevrez de nouveau les emails : {label}."
                : $"You will receive these emails again: {label}.",
            (false, true) => fr
                ? $"Vous êtes actuellement abonné(e) aux emails : {label}."
                : $"You are currently subscribed to these emails: {label}.",
            (false, false) => fr
                ? $"Vous n'êtes pas abonné(e) aux emails : {label}."
                : $"You are not subscribed to these emails: {label}.",
        };

        // The form flips the current state: subscribed users unsubscribe, unsubscribed users opt back in.
        var resubscribe = subscribed ? "false" : "true";
        var buttonLabel = subscribed
            ? (fr ? "Me désabonner" : "Unsubscribe")
            : (fr ? "Me réabonner" : "Resubscribe");

        var note = fr
            ? "Vous pouvez aussi tout gérer depuis vos paramètres de notification Jellyfin."
            : "You can also manage everything from your Jellyfin notification settings.";

        // Minimal HTML escaping only - the page is UTF-8 so accented characters pass through as-is.
        static string e(string value) => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(fr ? "fr" : "en").Append("\"><head>")
          .Append("<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<meta name=\"robots\" content=\"noindex\"><title>").Append(e(title)).Append("</title><style>")
          .Append("body{margin:0;background:Canvas;color:CanvasText;")
          .Append("font-family:system-ui,-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;line-height:1.5;}")
          .Append(".enotif-u-card{max-width:32rem;margin:3rem auto;padding:1.5rem 1.75rem;")
          .Append("border:1px solid GrayText;border-radius:0.5rem;}")
          .Append(".enotif-u-card h1{font-size:1.15rem;margin:0 0 0.75rem;}")
          .Append(".enotif-u-card p{margin:0.5rem 0;}")
          .Append(".enotif-u-btn{margin-top:1rem;padding:0.55rem 1rem;font:inherit;cursor:pointer;")
          .Append("background:ButtonFace;color:ButtonText;border:1px solid GrayText;border-radius:0.35rem;}")
          .Append(".enotif-u-note{margin-top:1.25rem;font-size:0.85rem;color:GrayText;}")
          .Append("</style></head><body><main class=\"enotif-u-card\">")
          .Append("<h1>").Append(e(title)).Append("</h1>");

        if (done)
        {
            sb.Append("<p><strong>").Append(e(fr ? "C'est fait." : "Done.")).Append("</strong></p>");
        }

        sb.Append("<p>").Append(e(state)).Append("</p>")
          .Append("<form method=\"post\">")
          .Append("<input type=\"hidden\" name=\"resubscribe\" value=\"").Append(resubscribe).Append("\">")
          .Append("<button type=\"submit\" class=\"enotif-u-btn\">").Append(e(buttonLabel)).Append("</button>")
          .Append("</form>")
          .Append("<p class=\"enotif-u-note\">").Append(e(note)).Append("</p>")
          .Append("</main></body></html>");

        return sb.ToString();
    }

    private static string CategoryLabel(string category, bool fr) => category switch
    {
        "news" => fr ? "Nouveautés de la médiathèque" : "New media",
        "recap" => fr ? "Résumé de la semaine" : "Weekly recap",
        _ => fr ? "tous les emails Easy Notif" : "all Easy Notif emails",
    };
}
