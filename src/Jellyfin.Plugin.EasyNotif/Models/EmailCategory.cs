using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.EasyNotif.Models;

/// <summary>
/// A kind of email a user can opt in to or out of. Manual admin announcements are not a category:
/// they are opt-out, tracked by <see cref="UserPreference.AnnouncementsOptOut"/>.
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
