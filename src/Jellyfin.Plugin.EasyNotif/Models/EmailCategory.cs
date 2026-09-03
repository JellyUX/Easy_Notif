using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.EasyNotif.Models;

/// <summary>
/// A kind of email a user can opt in to or out of. Manual admin emails are not a category:
/// every user with a valid contact address receives those (see Synthese.md section 7).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmailCategory
{
    /// <summary>The scheduled newsletter of newly added media.</summary>
    News,

    /// <summary>The personalised weekly watch recap.</summary>
    Recap
}

/// <summary>
/// Helpers over <see cref="EmailCategory"/>.
/// </summary>
public static class EmailCategories
{
    /// <summary>Gets every category, in a stable order.</summary>
    public static IReadOnlyList<EmailCategory> All { get; } = [EmailCategory.News, EmailCategory.Recap];
}
