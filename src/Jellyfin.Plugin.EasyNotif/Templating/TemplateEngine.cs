using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EasyNotif.Templating;

/// <summary>
/// A tiny, dependency-free template renderer for the email templates (Synthese.md section 3.5).
/// Supports <c>{{key}}</c> (HTML-escaped), <c>{{{key}}}</c> (raw), <c>{{#if key}}...{{/if}}</c>,
/// <c>{{#each key}}...{{/each}}</c> (with <c>{{.}}</c> and <c>{{@index}}</c> inside), and nesting.
/// Unknown keys render as an empty string.
/// </summary>
public static class TemplateEngine
{
    /// <summary>
    /// The maximum block nesting the renderer will descend into. Real templates nest two or three
    /// levels; the cap only exists so that a pathologically nested body (an admin footgun, or a
    /// crafted template) cannot exhaust the call stack and take the server process down with it.
    /// </summary>
    private const int MaxRenderDepth = 64;

    /// <summary>Renders a template against a model.</summary>
    /// <param name="template">The template text.</param>
    /// <param name="model">The root model. Values may be strings, numbers, booleans, nested
    /// dictionaries, or lists of dictionaries / scalars.</param>
    /// <returns>The rendered text.</returns>
    public static string Render(string template, IReadOnlyDictionary<string, object?> model)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(model);

        var output = new StringBuilder(template.Length);
        var scopes = new List<Frame> { new(model, null, -1) };
        RenderRegion(template, 0, template.Length, scopes, output, depth: 0);
        return output.ToString();
    }

    /// <summary>
    /// Returns the deepest block nesting in a template (a running max of open
    /// <c>{{#if}}</c> / <c>{{#each}}</c> minus their closers). Cheap; used to reject a
    /// pathologically nested body at save time rather than truncating it silently at render time.
    /// </summary>
    /// <param name="template">The template text.</param>
    /// <returns>The maximum nesting depth (0 for a flat template).</returns>
    public static int NestingDepth(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var depth = 0;
        var max = 0;
        var i = 0;
        while (true)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var close = template.IndexOf("}}", open, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var inner = template[(open + 2)..close].Trim().TrimStart('{');
            if (inner.StartsWith("#if ", StringComparison.Ordinal) || inner.StartsWith("#each ", StringComparison.Ordinal))
            {
                depth++;
                max = Math.Max(max, depth);
            }
            else if ((inner is "/if" or "/each") && depth > 0)
            {
                depth--;
            }

            i = close + 2;
        }

        return max;
    }

    private sealed record Frame(IReadOnlyDictionary<string, object?>? Data, object? Current, int Index);

    private static readonly Regex ReferencedKeyPattern = new(
        @"\{\{\{?\s*(?:#(?:if|each)\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns every model key a template refers to, from <c>{{key}}</c>, <c>{{{key}}}</c>,
    /// <c>{{#if key}}</c> and <c>{{#each key}}</c>. Closing tags, <c>{{.}}</c> and <c>{{@index}}</c>
    /// are not keys and are excluded. Used to validate an admin-edited template against the known
    /// placeholder set for its base id.
    /// </summary>
    /// <param name="template">The template text.</param>
    /// <returns>The distinct referenced keys.</returns>
    public static IReadOnlySet<string> ReferencedKeys(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ReferencedKeyPattern.Matches(template))
        {
            keys.Add(match.Groups[1].Value);
        }

        return keys;
    }

    private static void RenderRegion(string t, int start, int end, List<Frame> scopes, StringBuilder output, int depth)
    {
        if (depth > MaxRenderDepth)
        {
            // Stop descending rather than throw: a StackOverflowException is uncatchable and would
            // kill the process. The over-deep region is simply left unrendered.
            return;
        }

        var i = start;
        while (i < end)
        {
            var open = t.IndexOf("{{", i, end - i, StringComparison.Ordinal);
            if (open < 0)
            {
                output.Append(t, i, end - i);
                return;
            }

            output.Append(t, i, open - i);

            var raw = open + 2 < end && t[open + 2] == '{';
            var closeMarker = raw ? "}}}" : "}}";
            var close = t.IndexOf(closeMarker, open, end - open, StringComparison.Ordinal);
            if (close < 0)
            {
                output.Append(t, open, end - open);
                return;
            }

            var inner = t[(open + (raw ? 3 : 2))..close].Trim();
            var afterTag = close + closeMarker.Length;

            if (inner.StartsWith("#if ", StringComparison.Ordinal))
            {
                var key = inner[4..].Trim();
                var (bodyEnd, regionEnd) = FindBlockEnd(t, afterTag, end, "#if", "/if");
                if (IsTruthy(Resolve(scopes, key)))
                {
                    RenderRegion(t, afterTag, bodyEnd, scopes, output, depth + 1);
                }

                i = regionEnd;
            }
            else if (inner.StartsWith("#each ", StringComparison.Ordinal))
            {
                var key = inner[6..].Trim();
                var (bodyEnd, regionEnd) = FindBlockEnd(t, afterTag, end, "#each", "/each");
                if (Resolve(scopes, key) is IEnumerable list and not string)
                {
                    var index = 0;
                    foreach (var item in list)
                    {
                        scopes.Add(new Frame(item as IReadOnlyDictionary<string, object?>, item, index));
                        RenderRegion(t, afterTag, bodyEnd, scopes, output, depth + 1);
                        scopes.RemoveAt(scopes.Count - 1);
                        index++;
                    }
                }

                i = regionEnd;
            }
            else
            {
                var value = Stringify(Resolve(scopes, inner));
                output.Append(raw ? value : HtmlEscape(value));
                i = afterTag;
            }
        }
    }

    // Returns (index just before the matching close tag's "{{", index just after its "}}").
    private static (int BodyEnd, int RegionEnd) FindBlockEnd(string t, int from, int end, string openKw, string closeKw)
    {
        var depth = 1;
        var i = from;
        while (i < end)
        {
            var open = t.IndexOf("{{", i, end - i, StringComparison.Ordinal);
            if (open < 0)
            {
                return (end, end);
            }

            var close = t.IndexOf("}}", open, end - open, StringComparison.Ordinal);
            if (close < 0)
            {
                return (end, end);
            }

            var tag = t[(open + 2)..close].Trim().TrimStart('{');
            if (tag.StartsWith(openKw + " ", StringComparison.Ordinal) || tag == openKw)
            {
                depth++;
            }
            else if (tag == closeKw)
            {
                depth--;
                if (depth == 0)
                {
                    return (open, close + 2);
                }
            }

            i = close + 2;
        }

        return (end, end);
    }

    private static object? Resolve(List<Frame> scopes, string key)
    {
        if (key == ".")
        {
            for (var s = scopes.Count - 1; s >= 0; s--)
            {
                if (scopes[s].Current is not null)
                {
                    return scopes[s].Current;
                }
            }

            return null;
        }

        if (key == "@index")
        {
            for (var s = scopes.Count - 1; s >= 0; s--)
            {
                if (scopes[s].Index >= 0)
                {
                    return scopes[s].Index;
                }
            }

            return null;
        }

        for (var s = scopes.Count - 1; s >= 0; s--)
        {
            if (scopes[s].Data is { } data && data.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        IEnumerable e => e.Cast<object?>().Any(),
        _ => TryToDouble(value, out var d) ? d != 0 : true
    };

    // Neutralises HTML and attribute injection while leaving accented characters intact (the email
    // templates are UTF-8). Attributes in the templates are double-quoted, and the single quote is
    // escaped too for safety.
    private static string HtmlEscape(string value)
    {
        if (value.AsSpan().IndexOfAny("&<>\"'") < 0)
        {
            return value;
        }

        return new StringBuilder(value.Length + 16)
            .Append(value)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;")
            .ToString();
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static bool TryToDouble(object value, out double result)
    {
        try
        {
            result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            result = 0;
            return false;
        }
    }
}
