namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Builds the small bilingual message sent by the "Send a test" button. Deliberately plain: no
/// colours, no web fonts (R11). The dressed campaign templates arrive in Phase 10.
/// </summary>
public static class TestEmail
{
    /// <summary>Builds the test message subject and HTML body for a language.</summary>
    /// <param name="lang">The language, <c>"fr"</c> or anything else for English.</param>
    /// <returns>The subject and HTML body.</returns>
    public static (string Subject, string Html) Build(string? lang)
    {
        var french = string.Equals(lang, "fr", StringComparison.OrdinalIgnoreCase);

        return french
            ? ("Easy Notif - email de test",
               "<p>Ceci est un email de test envoye depuis Easy Notif.</p>"
               + "<p>Si vous le recevez, votre adresse de contact et le transport Resend "
               + "fonctionnent. Vous pouvez regler vos preferences dans les parametres de "
               + "votre compte Jellyfin.</p>")
            : ("Easy Notif - test email",
               "<p>This is a test email sent from Easy Notif.</p>"
               + "<p>If you received it, your contact address and the Resend transport are "
               + "working. You can manage your preferences in your Jellyfin account settings.</p>");
    }
}
