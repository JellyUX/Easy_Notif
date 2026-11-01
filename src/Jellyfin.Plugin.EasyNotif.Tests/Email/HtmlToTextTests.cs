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
    public void Convert_KeepsTheLinkTargetAfterItsLabel()
    {
        Assert.Equal(
            "Read the notes (https://media.example.org/notes?a=1&b=2) today",
            HtmlToText.Convert("<a href=\"https://media.example.org/notes?a=1&amp;b=2\" style=\"color:x\"><b>Read</b> the notes</a> today"));
        Assert.Equal("See (https://x.example)", HtmlToText.Convert("<A HREF='https://x.example'>See</A>"));
    }

    [Theory]
    [InlineData("<a href=\"mailto:admin@example.org\">Write to us</a>", "Write to us")]
    [InlineData("<a href=\"#top\">Back to top</a>", "Back to top")]
    [InlineData("<a href=\"https://x.example\">https://x.example</a>", "https://x.example")]
    [InlineData("<a href=\"https://x.example\"><img src=\"p.png\"></a>", "https://x.example")]
    [InlineData("<a name=\"top\">Anchor</a>", "Anchor")]
    public void Convert_DoesNotRepeatAPointlessTarget(string html, string expected)
    {
        Assert.Equal(expected, HtmlToText.Convert(html));
    }

    [Fact]
    public void Convert_CollapsesWhitespace()
    {
        Assert.Equal("a b c", HtmlToText.Convert("a     b\t\tc"));
    }
}
