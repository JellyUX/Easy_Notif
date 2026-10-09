using System.Net;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Templating;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Renders a manual email through the embedded <c>generic</c> template: the subject as the heading
/// (DEC-73), the admin's body inside the layout, and the unsubscribe link when the send carries one.
/// A plain-text body is escaped and split into paragraphs; an HTML body is inserted as written, so
/// it must have passed <see cref="ManualEmailService"/>'s validation first.
/// </summary>
public static class GenericEmailRenderer
{
    /// <summary>The base template id of the manual email layout.</summary>
    public const string TemplateId = "generic";

    /// <summary>Renders a manual email.</summary>
    /// <param name="subject">The subject, shown as the heading.</param>
    /// <param name="html">The admin's HTML body, or null.</param>
    /// <param name="text">The admin's plain-text body, used when <paramref name="html"/> is empty.</param>
    /// <param name="unsubscribeUrl">The unsubscribe link, or null for none.</param>
    /// <param name="french">True for French, false for English.</param>
    /// <param name="template">The <c>generic</c> template text in the same language.</param>
    /// <returns>The HTML body and the text body.</returns>
    public static (string Html, string Text) Render(string subject, string? html, string? text, string? unsubscribeUrl, bool french, string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var hasHtml = !string.IsNullOrWhiteSpace(html);
        var body = hasHtml ? html! : TextToHtml(text);
        var rendered = TemplateEngine.Render(template, Model(subject, body, unsubscribeUrl));

        var plain = new StringBuilder((hasHtml ? HtmlToText.Convert(html) : text ?? string.Empty).TrimEnd());
        if (unsubscribeUrl is not null)
        {
            plain.Append("\n\n")
                .Append(french ? "Ne plus recevoir d'emails de ce serveur" : "Stop all emails from this server")
                .Append(": ")
                .Append(unsubscribeUrl);
        }

        return (rendered, plain.Append('\n').ToString());
    }

    /// <summary>Returns the template model of a manual email.</summary>
    /// <param name="heading">The heading (the subject).</param>
    /// <param name="bodyHtml">The body, already HTML.</param>
    /// <param name="unsubscribeUrl">The unsubscribe link, or null.</param>
    /// <returns>The model.</returns>
    public static IReadOnlyDictionary<string, object?> Model(string heading, string bodyHtml, string? unsubscribeUrl)
        => new Dictionary<string, object?>
        {
            ["heading"] = heading,
            ["body"] = bodyHtml,
            ["unsubscribeUrl"] = unsubscribeUrl
        };

    /// <summary>Turns a plain-text body into escaped HTML paragraphs (blank line = new paragraph).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The HTML.</returns>
    public static string TextToHtml(string? text)
    {
        var paragraphs = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var html = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            html.Append("<p style=\"margin:0 0 12px;\">")
                .Append(WebUtility.HtmlEncode(paragraph).Replace("\n", "<br>", StringComparison.Ordinal))
                .Append("</p>");
        }

        return html.ToString();
    }
}
