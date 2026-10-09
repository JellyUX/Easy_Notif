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

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("no address here", "no address here")]
    [InlineData("erreur pour a.b@c.d", "erreur pour a***b@c.d")]
    [InlineData("to alice@example.org and ab@example.org.", "to a***e@example.org and ***@example.org.")]
    public void MaskInText_MasksEveryAddressAndKeepsTheRest(string? input, string? expected)
    {
        Assert.Equal(expected, EmailMasker.MaskInText(input));
    }

    [Fact]
    public void MaskUserId_KeepsTheFirstEightDigits_OfAGuidOrAGuidString()
    {
        var id = Guid.Parse("8f2c1a6e-41b0-4d0b-9a57-0c3f1e2d4b6a");

        Assert.Equal("8f2c1a6e", EmailMasker.MaskUserId(id));
        Assert.Equal("8f2c1a6e", EmailMasker.MaskUserId(id.ToString()));
        Assert.Equal("8f2c1a6e", EmailMasker.MaskUserId(id.ToString("N")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData(42)]
    public void MaskUserId_LeavesAnythingElseUnchanged(object? input)
    {
        Assert.Equal(input, EmailMasker.MaskUserId(input));
    }
}
