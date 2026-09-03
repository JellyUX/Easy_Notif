using System.Net.Mail;

namespace Jellyfin.Plugin.EasyNotif.Util;

/// <summary>
/// Masks a contact address for display in the admin table and in the dedicated log file
/// (R15: real addresses never appear above DEBUG level).
/// </summary>
public static class EmailMasker
{
    private const string None = "(none)";

    /// <summary>
    /// Masks an email address, keeping the first and last character of the local part and the whole
    /// domain: <c>samuel.lecomte37@gmail.com</c> becomes <c>s***7@gmail.com</c>. A null, blank, or
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
}
