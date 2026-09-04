using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Minimal HTML to plain-text conversion for the mandatory text part of an HTML email
/// (Synthese.md section 3.5). Not a full renderer: it strips markup, turns block-level ends into
/// line breaks and decodes entities. Good enough for a fallback body and for deliverability.
/// </summary>
public static partial class HtmlToText
{
    /// <summary>Converts an HTML fragment to plain text.</summary>
    /// <param name="html">The HTML. Null or empty yields an empty string.</param>
    /// <returns>The plain-text equivalent.</returns>
    public static string Convert(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = ScriptOrStyle().Replace(html, " ");
        text = LineBreakTag().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        var builder = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var collapsed = Whitespace().Replace(line, " ").Trim();
            if (collapsed.Length > 0)
            {
                builder.Append(collapsed).Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</li>|</tr>|</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex Whitespace();
}
