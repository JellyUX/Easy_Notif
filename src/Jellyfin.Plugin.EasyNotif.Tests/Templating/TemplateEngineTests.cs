using Jellyfin.Plugin.EasyNotif.Templating;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Templating;

/// <summary>
/// Covers <see cref="TemplateEngine"/>: escaping vs raw output, truthiness of <c>{{#if}}</c>,
/// <c>{{#each}}</c> over 0/1/N items, <c>{{.}}</c> and <c>{{@index}}</c>, nested blocks, parent
/// scope fallback, and unknown keys rendering empty.
/// </summary>
public sealed class TemplateEngineTests
{
    private static string R(string template, object model)
        => TemplateEngine.Render(template, ToDict(model));

    private static Dictionary<string, object?> ToDict(object o)
        => o as Dictionary<string, object?> ?? o.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(o));

    [Fact]
    public void DoubleBrace_EscapesHtml()
        => Assert.Equal("&lt;b&gt; &amp; &quot;x&quot;", R("{{v}}", new { v = "<b> & \"x\"" }));

    [Fact]
    public void TripleBrace_EmitsRaw()
        => Assert.Equal("<b>x</b>", R("{{{v}}}", new { v = "<b>x</b>" }));

    [Fact]
    public void UnknownKey_IsEmpty()
        => Assert.Equal("[]", R("[{{missing}}]", new { v = 1 }));

    [Theory]
    [InlineData(true, "YES")]
    [InlineData(false, "")]
    public void If_Bool(bool flag, string expected)
        => Assert.Equal(expected, R("{{#if f}}YES{{/if}}", new { f = flag }));

    [Fact]
    public void If_EmptyStringAndZeroAreFalsy_NonEmptyCollectionIsTruthy()
    {
        Assert.Equal("", R("{{#if s}}x{{/if}}", new { s = "" }));
        Assert.Equal("", R("{{#if n}}x{{/if}}", new { n = 0 }));
        Assert.Equal("x", R("{{#if list}}x{{/if}}", new Dictionary<string, object?> { ["list"] = new[] { 1 } }));
        Assert.Equal("", R("{{#if list}}x{{/if}}", new Dictionary<string, object?> { ["list"] = Array.Empty<int>() }));
    }

    [Fact]
    public void Each_OverZeroOneAndThree()
    {
        var t = "{{#each items}}[{{.}}]{{/each}}";
        Assert.Equal("", TemplateEngine.Render(t, new Dictionary<string, object?> { ["items"] = Array.Empty<string>() }));
        Assert.Equal("[a]", TemplateEngine.Render(t, new Dictionary<string, object?> { ["items"] = new[] { "a" } }));
        Assert.Equal("[a][b][c]", TemplateEngine.Render(t, new Dictionary<string, object?> { ["items"] = new[] { "a", "b", "c" } }));
    }

    [Fact]
    public void Each_ExposesIndexAndItemProperties()
    {
        var model = new Dictionary<string, object?>
        {
            ["rows"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "A" },
                new Dictionary<string, object?> { ["name"] = "B" }
            }
        };

        Assert.Equal("0:A 1:B ", TemplateEngine.Render("{{#each rows}}{{@index}}:{{name}} {{/each}}", model));
    }

    [Fact]
    public void Each_NestsAnIf_UsingItemScope()
    {
        var model = new Dictionary<string, object?>
        {
            ["rows"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "A", ["link"] = "/a" },
                new Dictionary<string, object?> { ["name"] = "B", ["link"] = null }
            }
        };

        var t = "{{#each rows}}{{#if link}}<a>{{name}}</a>{{/if}}{{#if noLink}}{{/if}}{{/each}}";
        Assert.Equal("<a>A</a>", TemplateEngine.Render(t, model));
    }

    [Fact]
    public void Each_CanStillSeeParentScopeKeys()
    {
        var model = new Dictionary<string, object?>
        {
            ["suffix"] = "!",
            ["items"] = new[] { new Dictionary<string, object?> { ["x"] = "a" } }
        };

        Assert.Equal("a!", TemplateEngine.Render("{{#each items}}{{x}}{{suffix}}{{/each}}", model));
    }

    [Fact]
    public void If_NestsAnEach()
    {
        var model = new Dictionary<string, object?>
        {
            ["show"] = true,
            ["items"] = new[] { "a", "b" }
        };

        Assert.Equal("a,b,", TemplateEngine.Render("{{#if show}}{{#each items}}{{.}},{{/each}}{{/if}}", model));
    }

    [Fact]
    public void ReferencedKeys_CollectsValuesConditionsAndLoops_ExcludesClosersDotAndIndex()
    {
        const string Template =
            "{{heading}} {{{rawBody}}} {{#if hasItems}}{{#each items}}{{.}} {{@index}} {{name}}{{/each}}{{/if}}{{/if}}";

        var keys = TemplateEngine.ReferencedKeys(Template);

        Assert.Equal(
            new[] { "hasItems", "heading", "items", "name", "rawBody" },
            keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void ReferencedKeys_DeduplicatesRepeatedKeys()
        => Assert.Equal(["title"], TemplateEngine.ReferencedKeys("{{title}}{{title}}{{#if title}}x{{/if}}"));

    [Fact]
    public void ReferencedKeys_IsEmptyForAPlainString()
        => Assert.Empty(TemplateEngine.ReferencedKeys("no placeholders here"));

    // R10: an admin-edited template with broken markup must degrade, never throw - the same
    // Render call feeds both the preview endpoint and the live send path.
    [Theory]
    [InlineData("{{#if flag}}open but never closed")]
    [InlineData("no opener {{/if}} dangling closer")]
    [InlineData("{{#each rows}}{{name}}")]
    [InlineData("unterminated {{ mustache")]
    [InlineData("{{{raw without end")]
    [InlineData("{{#if a}}{{#each b}}{{#if c}}deeply {{/if}} unbalanced")]
    public void Render_MalformedMarkup_DoesNotThrow(string template)
    {
        var model = new Dictionary<string, object?>
        {
            ["flag"] = true,
            ["rows"] = new[] { new Dictionary<string, object?> { ["name"] = "x" } },
            ["a"] = true,
            ["b"] = new[] { new Dictionary<string, object?> { ["c"] = true } }
        };

        var ex = Record.Exception(() => TemplateEngine.Render(template, model));

        Assert.Null(ex);
    }
}
