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
        text = Link().Replace(text, KeepLinkTarget);
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

    // "<a href="u">label</a>" becomes "label (u)", so the text part keeps the link target. Mail
    // links, in-page anchors and links whose label already is the URL keep the label only.
    private static string KeepLinkTarget(Match match)
    {
        var href = match.Groups["href"].Value.Trim();
        var label = match.Groups["label"].Value;
        var decodedHref = WebUtility.HtmlDecode(href);
        var labelText = WebUtility.HtmlDecode(AnyTag().Replace(label, string.Empty)).Trim();
        if (decodedHref.Length == 0
            || decodedHref.StartsWith('#')
            || decodedHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(decodedHref, labelText, StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        return labelText.Length == 0 ? href : $"{label} ({href})";
    }

    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*(?<q>[""'])(?<href>.*?)\k<q>[^>]*>(?<label>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Link();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</li>|</tr>|</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex Whitespace();
}
