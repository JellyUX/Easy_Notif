using Jellyfin.Plugin.EasyNotif.Email;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="UnsubscribeToken"/>: a token round-trips, any tampering or a wrong secret is
/// rejected, garbage is rejected without throwing, and the secret never appears in the token.
/// </summary>
public class UnsubscribeTokenTests
{
    private const string Secret = "s3cr3t-unsubscribe-key-abcdef1234567890";
    private static readonly Guid UserId = Guid.Parse("7a27339e-e774-4969-9755-8cd213dcc5e7");

    [Fact]
    public void Create_ThenVerify_RoundTrips()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "all");

        Assert.True(UnsubscribeToken.TryVerify(Secret, token, out var userId, out var category));
        Assert.Equal(UserId, userId);
        Assert.Equal("all", category);
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "news");
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        var tampered = "AAAA" + token[4..dot] + token[dot..];

        Assert.False(UnsubscribeToken.TryVerify(Secret, tampered, out _, out _));
    }

    [Fact]
    public void Verify_TamperedSignature_ReturnsFalse()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "recap");
        var flipped = token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1];

        Assert.False(UnsubscribeToken.TryVerify(Secret, flipped, out _, out _));
    }

    [Fact]
    public void Verify_WrongSecret_ReturnsFalse()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "all");

        Assert.False(UnsubscribeToken.TryVerify("another-secret", token, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-dot-here")]
    [InlineData(".")]
    [InlineData("a.")]
    [InlineData("not base64!.also not base64!")]
    public void Verify_Garbage_ReturnsFalseWithoutThrowing(string token)
    {
        Assert.False(UnsubscribeToken.TryVerify(Secret, token, out _, out _));
    }

    [Fact]
    public void Create_DoesNotEmbedTheSecret()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "all");

        Assert.DoesNotContain(Secret, token, StringComparison.Ordinal);
    }
}
