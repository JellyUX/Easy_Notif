using Jellyfin.Plugin.EasyNotif.Email;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>Covers <see cref="HtmlToText.Convert"/>: markup removal, block breaks, entity decoding.</summary>
public class HtmlToTextTests
{
    [Fact]
    public void Convert_NullOrEmpty_YieldsEmptyString()
    {
        Assert.Equal(string.Empty, HtmlToText.Convert(null));
        Assert.Equal(string.Empty, HtmlToText.Convert(string.Empty));
    }

    [Fact]
    public void Convert_StripsInlineTags()
    {
        Assert.Equal("Hello world", HtmlToText.Convert("<span>Hello</span> <b>world</b>"));
    }

    [Fact]
    public void Convert_TurnsBlockEndsIntoLineBreaks()
    {
        var result = HtmlToText.Convert("<p>One</p><p>Two</p><div>Three</div><br>Four");
        Assert.Equal("One\nTwo\nThree\nFour", result);
    }

    [Fact]
    public void Convert_DecodesEntities()
    {
        Assert.Equal("Tom & Jerry", HtmlToText.Convert("Tom &amp; Jerry"));
        Assert.Equal("cafe", HtmlToText.Convert("caf&#101;"));
    }

    [Fact]
    public void Convert_DropsScriptAndStyleContent()
    {
        var result = HtmlToText.Convert("<style>p{color:red}</style><p>Body</p><script>alert(1)</script>");
        Assert.Equal("Body", result);
        Assert.DoesNotContain("color", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_CollapsesWhitespace()
    {
        Assert.Equal("a b c", HtmlToText.Convert("a     b\t\tc"));
    }
}
