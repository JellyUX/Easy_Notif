using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.EasyNotif.Webhooks;

/// <summary>
/// Verifies a Resend (svix / standard-webhooks) signature. The secret is the base64 payload after
/// the <c>whsec_</c> prefix; the signed content is <c>"{id}.{timestamp}.{body}"</c> HMAC-SHA256'd
/// and base64-encoded; the signature header holds space-separated <c>v1,&lt;base64&gt;</c> entries.
/// Comparison is constant-time and a stale timestamp is rejected. Never throws.
/// </summary>
public static class SvixVerifier
{
    private const string SecretPrefix = "whsec_";

    /// <summary>Verifies a webhook signature.</summary>
    /// <param name="signingSecret">The configured signing secret (<c>whsec_...</c>).</param>
    /// <param name="id">The <c>svix-id</c> / <c>webhook-id</c> header.</param>
    /// <param name="timestamp">The <c>svix-timestamp</c> / <c>webhook-timestamp</c> header (epoch seconds).</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="signatureHeader">The <c>svix-signature</c> / <c>webhook-signature</c> header.</param>
    /// <param name="now">The current time.</param>
    /// <param name="tolerance">The maximum allowed clock skew.</param>
    /// <returns>True when the signature is valid and the timestamp is within tolerance.</returns>
    public static bool Verify(
        string? signingSecret,
        string? id,
        string? timestamp,
        string? body,
        string? signatureHeader,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signingSecret)
            || string.IsNullOrEmpty(id)
            || string.IsNullOrEmpty(timestamp)
            || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        if (!long.TryParse(timestamp, out var epoch))
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(epoch)).Duration() > tolerance)
        {
            return false;
        }

        byte[] key;
        try
        {
            var raw = signingSecret.StartsWith(SecretPrefix, StringComparison.Ordinal)
                ? signingSecret[SecretPrefix.Length..]
                : signingSecret;
            key = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expected;
        using (var hmac = new HMACSHA256(key))
        {
            expected = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
        }

        foreach (var entry in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = entry.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0 || entry[..comma] != "v1")
            {
                continue;
            }

            byte[] provided;
            try
            {
                provided = Convert.FromBase64String(entry[(comma + 1)..]);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                return true;
            }
        }

        return false;
    }
}
