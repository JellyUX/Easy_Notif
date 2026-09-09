using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.EasyNotif.Util;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Opaque one-click unsubscribe token: <c>base64url(payloadBytes ++ HMAC-SHA256(secret, payloadBytes))</c>
/// where <c>payload = "{userId:N}:{category}"</c> and the trailing 32 bytes are the signature
/// (Synthese.md section 7.2). One flat base64url segment, no separator, so email link rewriters and
/// URL normalizers cannot mangle it. The scope is a single preference toggle, so no Jellyfin auth
/// is needed to act on it.
/// </summary>
public static class UnsubscribeToken
{
    private const int SignatureBytes = 32; // HMAC-SHA256

    /// <summary>Builds a token for a user and a category.</summary>
    /// <param name="secret">The plugin unsubscribe secret (from <see cref="Configuration.ISecretStore"/>).</param>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="category">The category to unsubscribe from: <c>news</c>, <c>recap</c> or <c>all</c>.</param>
    /// <returns>The token.</returns>
    public static string Create(string secret, Guid userId, string category)
    {
        var payload = Encoding.UTF8.GetBytes($"{userId:N}:{category}");
        var signature = Sign(secret, payload);
        var blob = new byte[payload.Length + signature.Length];
        payload.CopyTo(blob, 0);
        signature.CopyTo(blob, payload.Length);
        return ToBase64Url(blob);
    }

    /// <summary>Verifies a token and extracts its user id and category.</summary>
    /// <param name="secret">The plugin unsubscribe secret.</param>
    /// <param name="token">The token.</param>
    /// <param name="userId">The user id, when valid.</param>
    /// <param name="category">The category, when valid.</param>
    /// <returns>True when the token is well-formed and correctly signed.</returns>
    public static bool TryVerify(string secret, string? token, out Guid userId, out string category)
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

        var payload = blob[..^SignatureBytes];
        var providedSignature = blob[^SignatureBytes..];

        if (!CryptographicOperations.FixedTimeEquals(providedSignature, Sign(secret, payload)))
        {
            return false;
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
