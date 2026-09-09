using Jellyfin.Plugin.EasyNotif.Util;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Util;

/// <summary>
/// Covers <see cref="PublicUrl.IsValid"/>: an absolute http/https URL with no control characters is
/// accepted; anything else (blank, relative, wrong scheme, embedded CR/LF) is rejected.
/// </summary>
public class PublicUrlTests
{
    [Theory]
    [InlineData("http://localhost:8099")]
    [InlineData("https://media.example.org")]
    [InlineData("https://media.example.org/")]
    [InlineData("https://example.org:8443/base")]
    public void IsValid_AcceptsAbsoluteHttpUrls(string value)
        => Assert.True(PublicUrl.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("media.example.org")]
    [InlineData("//media.example.org")]
    [InlineData("ftp://media.example.org")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    public void IsValid_RejectsNonAbsoluteOrNonHttp(string? value)
        => Assert.False(PublicUrl.IsValid(value));

    [Theory]
    [InlineData("https://media.example.org\r\nX-Injected: 1")]
    [InlineData("https://media.example.org\nX-Injected: 1")]
    [InlineData("https://media.example.org\tvalue")]
    public void IsValid_RejectsControlCharacters(string value)
        => Assert.False(PublicUrl.IsValid(value));
}
