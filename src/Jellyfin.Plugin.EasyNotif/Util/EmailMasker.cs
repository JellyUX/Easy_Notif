using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EasyNotif.Util;

/// <summary>
/// Masks personal data for display in the admin table and in every log line: contact addresses,
/// whole or inside free text, and user ids (PRIV-03).
/// </summary>
public static partial class EmailMasker
{
    private const string None = "(none)";
    private const string MaskMarker = "***";
    private const int UserIdPrefixLength = 8;

    /// <summary>
    /// Masks an email address, keeping the first and last character of the local part and the whole
    /// domain: <c>jane.doe42@example.com</c> becomes <c>j***2@example.com</c>. A null, blank, or
    /// unparseable address becomes <c>(none)</c>.
    /// </summary>
    /// <param name="email">The address to mask.</param>
    /// <returns>The masked address, or <c>(none)</c>.</returns>
    public static string Mask(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email.Trim(), out var parsed))
        {
            return None;
        }

        var local = parsed.User;
        var domain = parsed.Host;

        return local.Length <= 2
            ? $"***@{domain}"
            : $"{local[0]}***{local[^1]}@{domain}";
    }

    /// <summary>
    /// Masks every address found in a free text, such as a provider error message, with
    /// <see cref="Mask(string?)"/>; the rest of the text is kept. An address that is already masked
    /// is left as it is, so masking twice gives the same text.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <returns>The text with every address masked, or the input when it is null or empty.</returns>
    public static string? MaskInText(string? text)
        => string.IsNullOrEmpty(text) || !text.Contains('@', StringComparison.Ordinal)
            ? text
            : AddressPattern().Replace(text, match =>
            {
                if (match.Value.Contains(MaskMarker, StringComparison.Ordinal))
                {
                    return match.Value;
                }

                var masked = Mask(match.Value);
                return masked == None ? $"***@{match.Value[(match.Value.IndexOf('@', StringComparison.Ordinal) + 1)..]}" : masked;
            });

    /// <summary>
    /// Shortens a user id to its first eight hexadecimal digits, enough to cross-check with the admin
    /// users table without logging the full identifier.
    /// </summary>
    /// <param name="userId">A <see cref="Guid"/> or a string holding one.</param>
    /// <returns>The shortened id, or <paramref name="userId"/> unchanged when it is not a GUID.</returns>
    public static object? MaskUserId(object? userId) => userId switch
    {
        Guid id => Shorten(id),
        string s when Guid.TryParse(s, out var id) => Shorten(id),
        _ => userId
    };

    private static string Shorten(Guid id)
        => id.ToString("N", CultureInfo.InvariantCulture)[..UserIdPrefixLength];

    [GeneratedRegex(@"[A-Za-z0-9._%+*-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();
}
