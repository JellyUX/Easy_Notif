using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Email;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="UnsubscribeToken"/>: a token round-trips, any tampering or a wrong secret is
/// rejected, garbage is rejected without throwing, the token is a single url-safe segment (no dot),
/// and the secret never appears in the token.
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
    public void Create_ProducesASingleUrlSafeSegment()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "news");

        Assert.Matches("^[A-Za-z0-9_-]+$", token); // base64url only, no dot or slash
    }

    [Fact]
    public void Verify_TamperedPayload_ReturnsFalse()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "news");
        var tampered = "AAAA" + token[4..];

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
    [InlineData("AAAA")]
    [InlineData("not base64!")]
    [InlineData("....")]
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

    [Fact]
    public void V2_StartsWithTheVersionByte()
        => Assert.Equal(0x02, FromBase64Url(UnsubscribeToken.Create(Secret, UserId, "news"))[0]);

    [Fact]
    public void V2_ValidUpTo180Days_RejectedAt181()
    {
        var token = UnsubscribeToken.Create(Secret, UserId, "news", Issued);

        Assert.True(UnsubscribeToken.TryVerify(Secret, token, Issued.AddDays(180), out _, out _));
        Assert.False(UnsubscribeToken.TryVerify(Secret, token, Issued.AddDays(181), out var userId, out var category));
        Assert.Equal(Guid.Empty, userId);
        Assert.Equal(string.Empty, category);
    }

    [Fact]
    public void V2_ForgedIssueDate_FailsTheSignature()
    {
        var blob = FromBase64Url(UnsubscribeToken.Create(Secret, UserId, "news", Issued));
        blob[8] ^= 0x01; // last byte of the issue timestamp

        Assert.False(UnsubscribeToken.TryVerify(Secret, ToBase64Url(blob), Issued, out _, out _));
    }

    [Fact]
    public void V1Legacy_AcceptedUntilTheCutoff_RejectedAfter()
    {
        var legacy = LegacyToken(UserId, "all");

        Assert.True(UnsubscribeToken.TryVerify(Secret, legacy, UnsubscribeToken.LegacyAcceptedUntil, out var userId, out var category));
        Assert.Equal(UserId, userId);
        Assert.Equal("all", category);
        Assert.False(UnsubscribeToken.TryVerify(Secret, legacy, UnsubscribeToken.LegacyAcceptedUntil.AddSeconds(1), out _, out _));
    }

    [Fact]
    public void ListUnsubscribeHeaders_OverHttps_OfferOneClick()
    {
        var headers = UnsubscribeToken.BuildListUnsubscribeHeaders("https://media.example.org/", Secret, UserId, "news");

        Assert.NotNull(headers);
        Assert.StartsWith("<https://media.example.org/EasyNotif/u/", headers["List-Unsubscribe"], StringComparison.Ordinal);
        Assert.Equal("List-Unsubscribe=One-Click", headers["List-Unsubscribe-Post"]);
    }

    [Fact]
    public void ListUnsubscribeHeaders_OverHttp_KeepTheLink_WithoutOneClick()
    {
        // RFC 8058 section 3.1: the one-click header requires an HTTPS URI.
        var headers = UnsubscribeToken.BuildListUnsubscribeHeaders("http://localhost:8099", Secret, UserId, "news");

        Assert.NotNull(headers);
        Assert.StartsWith("<http://localhost:8099/EasyNotif/u/", headers["List-Unsubscribe"], StringComparison.Ordinal);
        Assert.False(headers.ContainsKey("List-Unsubscribe-Post"));
    }

    private static readonly DateTimeOffset Issued = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    // The 1.0.x format: base64url("{userId:N}:{category}" ++ HMAC-SHA256(secret, that)).
    private static string LegacyToken(Guid userId, string category)
    {
        var payload = Encoding.UTF8.GetBytes($"{userId:N}:{category}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return ToBase64Url([.. payload, .. hmac.ComputeHash(payload)]);
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }
}
