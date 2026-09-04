using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Webhooks;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Webhooks;

/// <summary>
/// Covers <see cref="SvixVerifier.Verify"/>: a correctly signed payload passes, any tampering or a
/// stale timestamp fails, a multi-entry header passes when one entry matches, and a malformed secret
/// returns false rather than throwing.
/// </summary>
public class SvixVerifierTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static string Timestamp(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Sign(string secret, string id, string timestamp, string body)
    {
        var raw = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
        using var hmac = new HMACSHA256(Convert.FromBase64String(raw));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
        return "v1," + Convert.ToBase64String(mac);
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var ts = Timestamp(Now);
        const string Body = "{\"type\":\"email.delivered\"}";

        Assert.True(SvixVerifier.Verify(Secret, "msg_1", ts, Body, Sign(Secret, "msg_1", ts, Body), Now, Tolerance));
    }

    [Fact]
    public void Verify_TamperedBody_ReturnsFalse()
    {
        var ts = Timestamp(Now);
        var signature = Sign(Secret, "msg_1", ts, "{\"type\":\"email.delivered\"}");

        Assert.False(SvixVerifier.Verify(Secret, "msg_1", ts, "{\"type\":\"email.bounced\"}", signature, Now, Tolerance));
    }

    [Fact]
    public void Verify_TamperedSignature_ReturnsFalse()
    {
        var ts = Timestamp(Now);
        const string Body = "{}";
        var good = Sign(Secret, "msg_1", ts, Body);
        var bad = good[..^2] + (good[^2] == 'A' ? "B=" : "A=");

        Assert.False(SvixVerifier.Verify(Secret, "msg_1", ts, Body, bad, Now, Tolerance));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(-6, false)]
    [InlineData(4, true)]
    public void Verify_ChecksTheTimestampTolerance(int minutesOff, bool expected)
    {
        var signedAt = Now.AddMinutes(minutesOff);
        var ts = Timestamp(signedAt);
        const string Body = "{}";

        Assert.Equal(
            expected,
            SvixVerifier.Verify(Secret, "msg_1", ts, Body, Sign(Secret, "msg_1", ts, Body), Now, Tolerance));
    }

    [Fact]
    public void Verify_HeaderWithMultipleEntries_PassesWhenOneMatches()
    {
        var ts = Timestamp(Now);
        const string Body = "{}";
        var good = Sign(Secret, "msg_1", ts, Body);
        var header = "v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= " + good;

        Assert.True(SvixVerifier.Verify(Secret, "msg_1", ts, Body, header, Now, Tolerance));
    }

    [Fact]
    public void Verify_HeaderWithOnlyOtherVersions_ReturnsFalse()
    {
        var ts = Timestamp(Now);
        const string Body = "{}";
        var v1 = Sign(Secret, "msg_1", ts, Body)["v1,".Length..];

        Assert.False(SvixVerifier.Verify(Secret, "msg_1", ts, Body, "v2," + v1, Now, Tolerance));
    }

    [Fact]
    public void Verify_SecretWithoutPrefix_IsAccepted()
    {
        var bare = Secret["whsec_".Length..];
        var ts = Timestamp(Now);
        const string Body = "{}";

        Assert.True(SvixVerifier.Verify(bare, "msg_1", ts, Body, Sign(bare, "msg_1", ts, Body), Now, Tolerance));
    }

    [Fact]
    public void Verify_MalformedSecret_ReturnsFalseWithoutThrowing()
    {
        var ts = Timestamp(Now);

        Assert.False(SvixVerifier.Verify("whsec_not base64!", "msg_1", ts, "{}", "v1,AAAA", Now, Tolerance));
    }

    [Fact]
    public void Verify_MissingInputs_ReturnFalse()
    {
        Assert.False(SvixVerifier.Verify(Secret, null, "1", "{}", "v1,x", Now, Tolerance));
        Assert.False(SvixVerifier.Verify(null, "msg", "1", "{}", "v1,x", Now, Tolerance));
        Assert.False(SvixVerifier.Verify(Secret, "msg", "1", "{}", null, Now, Tolerance));
    }
}
