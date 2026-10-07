using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Util;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Opaque one-click unsubscribe token: <c>base64url(signed ++ HMAC-SHA256(secret, signed))</c>,
/// the trailing 32 bytes being the signature (Synthese.md section 7.2). One flat base64url segment,
/// no separator, so email link rewriters and URL normalizers cannot mangle it. The scope is a single
/// preference toggle, so no Jellyfin auth is needed to act on it.
/// <list type="bullet">
/// <item>v2 (issued now): <c>signed = 0x02 ++ issuedAtUnixSeconds (8 bytes, big-endian) ++ "{userId:N}:{category}"</c>,
/// valid <see cref="Lifetime"/> after issue.</item>
/// <item>v1 (1.0.x, undated): <c>signed = "{userId:N}:{category}"</c>, accepted until
/// <see cref="LegacyAcceptedUntil"/> so links in mail sent before the upgrade keep working. Its
/// first byte is an ASCII hex digit, so it can never be mistaken for the 0x02 version byte.</item>
/// </list>
/// </summary>
public static class UnsubscribeToken
{
    /// <summary>How long a v2 token stays valid after it is issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    /// <summary>The last instant an undated v1 token is accepted.</summary>
    public static readonly DateTimeOffset LegacyAcceptedUntil = new(2027, 6, 30, 23, 59, 59, TimeSpan.Zero);

    private const int SignatureBytes = 32; // HMAC-SHA256
    private const byte Version2 = 0x02;
    private const int V2HeaderBytes = 1 + sizeof(long);
    private const long MaxUnixSeconds = 253402300799; // 9999-12-31T23:59:59Z, the DateTimeOffset maximum

    /// <summary>Builds a v2 token for a user and a category.</summary>
    /// <param name="secret">The plugin unsubscribe secret (from <see cref="Configuration.ISecretStore"/>).</param>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="category">The category to unsubscribe from: <c>news</c>, <c>recap</c> or <c>all</c>.</param>
    /// <param name="issuedAt">The issue time; null means now.</param>
    /// <returns>The token.</returns>
    public static string Create(string secret, Guid userId, string category, DateTimeOffset? issuedAt = null)
    {
        var payload = Encoding.UTF8.GetBytes($"{userId:N}:{category}");
        var signed = new byte[V2HeaderBytes + payload.Length];
        signed[0] = Version2;
        BinaryPrimitives.WriteInt64BigEndian(signed.AsSpan(1), (issuedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        payload.CopyTo(signed, V2HeaderBytes);
        return ToBase64Url([.. signed, .. Sign(secret, signed)]);
    }

    /// <summary>Verifies a token against the current time. See <see cref="TryVerify(string, string?, DateTimeOffset, out Guid, out string)"/>.</summary>
    /// <param name="secret">The plugin unsubscribe secret.</param>
    /// <param name="token">The token.</param>
    /// <param name="userId">The user id, when valid.</param>
    /// <param name="category">The category, when valid.</param>
    /// <returns>True when the token is well-formed, correctly signed and not expired.</returns>
    public static bool TryVerify(string secret, string? token, out Guid userId, out string category)
        => TryVerify(secret, token, DateTimeOffset.UtcNow, out userId, out category);

    /// <summary>Verifies a token and extracts its user id and category.</summary>
    /// <param name="secret">The plugin unsubscribe secret.</param>
    /// <param name="token">The token.</param>
    /// <param name="now">The current time, for expiry.</param>
    /// <param name="userId">The user id, when valid.</param>
    /// <param name="category">The category, when valid.</param>
    /// <returns>True when the token is well-formed, correctly signed and not expired.</returns>
    public static bool TryVerify(string secret, string? token, DateTimeOffset now, out Guid userId, out string category)
    {
        userId = Guid.Empty;
        category = string.Empty;

        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        byte[] blob;
        try
        {
            blob = FromBase64Url(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (blob.Length <= SignatureBytes)
        {
            return false;
        }

        var signed = blob[..^SignatureBytes];
        var providedSignature = blob[^SignatureBytes..];

        if (!CryptographicOperations.FixedTimeEquals(providedSignature, Sign(secret, signed)))
        {
            return false;
        }

        byte[] payload;
        if (signed[0] == Version2)
        {
            if (signed.Length <= V2HeaderBytes)
            {
                return false;
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(
                Math.Clamp(BinaryPrimitives.ReadInt64BigEndian(signed.AsSpan(1)), 0, MaxUnixSeconds));
            if (now - issuedAt > Lifetime)
            {
                return false;
            }

            payload = signed[V2HeaderBytes..];
        }
        else
        {
            if (now > LegacyAcceptedUntil)
            {
                return false;
            }

            payload = signed;
        }

        var parts = Encoding.UTF8.GetString(payload).Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out userId))
        {
            userId = Guid.Empty;
            return false;
        }

        category = parts[1];
        return true;
    }

    /// <summary>
    /// Builds the <c>List-Unsubscribe</c> / <c>List-Unsubscribe-Post</c> header pair for a
    /// one-click unsubscribe link, or null when the secret is not set or the public server URL is
    /// missing or not a valid http/https URL (Synthese.md section 7.2). Consumed by
    /// <c>GET|POST /EasyNotif/u/{token}</c>.
    /// </summary>
    /// <param name="publicServerUrl">The public base URL of this server.</param>
    /// <param name="secret">The plugin unsubscribe secret.</param>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="category">The category to unsubscribe from: <c>news</c>, <c>recap</c> or <c>all</c>.</param>
    /// <returns>The header pair, or null.</returns>
    public static IReadOnlyDictionary<string, string>? BuildListUnsubscribeHeaders(
        string? publicServerUrl,
        string? secret,
        Guid userId,
        string category)
    {
        if (string.IsNullOrWhiteSpace(secret) || !PublicUrl.IsValid(publicServerUrl))
        {
            return null;
        }

        var token = Create(secret, userId, category);
        var url = $"{publicServerUrl.TrimEnd('/')}/EasyNotif/u/{token}";
        return new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = $"<{url}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
        };
    }

    private static byte[] Sign(string secret, byte[] payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(payload);
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
