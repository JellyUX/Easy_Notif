using Jellyfin.Plugin.EasyNotif.Util;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Util;

/// <summary>
/// Covers <see cref="EmailMasker.Mask"/>: keep the ends of the local part and the whole domain,
/// collapse a short local part, and fall back to <c>(none)</c> for anything unusable.
/// </summary>
public class EmailMaskerTests
{
    [Theory]
    [InlineData("samuel.lecomte37@gmail.com", "s***7@gmail.com")]
    [InlineData("alice@example.org", "a***e@example.org")]
    [InlineData("ab@example.org", "***@example.org")]
    [InlineData("a@example.org", "***@example.org")]
    public void Mask_KeepsTheEndsOfTheLocalPartAndTheWholeDomain(string input, string expected)
    {
        Assert.Equal(expected, EmailMasker.Mask(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("nope@")]
    [InlineData("@nope")]
    public void Mask_ReturnsNone_ForAnythingUnusable(string? input)
    {
        Assert.Equal("(none)", EmailMasker.Mask(input));
    }
}
