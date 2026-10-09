namespace Jellyfin.Plugin.EasyNotif.Models;

/// <summary>
/// One user's email preferences. A user with no stored record is opted out of every category and
/// has no contact address (see Synthese.md section 0). The record is created the first time the
/// user sets an address or toggles a category.
/// </summary>
public sealed class UserPreference
{
    /// <summary>Gets or sets the Jellyfin user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the address this user wants their email sent to. Null or empty means the user
    /// is excluded from every send until they set one.
    /// </summary>
    public string? ContactEmail { get; set; }

    /// <summary>
    /// Gets the per-category opt-in state. A missing key is treated as opted out.
    /// </summary>
    public Dictionary<EmailCategory, bool> Categories { get; init; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the user opted out of manual admin announcements
    /// sent to everyone. Unlike <see cref="Categories"/> the default is opted in, and it is stored
    /// outside that dictionary so a file written by this version stays readable by 1.0.2 (an
    /// unknown enum key would fail its parse; an unknown property is ignored).
    /// </summary>
    public bool AnnouncementsOptOut { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user opted out of every new-episode email of the
    /// series they follow (the global "new episodes" category). Opt-out like announcements, and kept
    /// outside <see cref="Categories"/> for the same 1.0.2 compatibility reason.
    /// </summary>
    public bool EpisodesOptOut { get; set; }

    /// <summary>
    /// Gets or sets the UTC time Resend reported <see cref="ContactEmail"/> as permanently
    /// undeliverable (hard bounce or suppression, DEC-49), or null while it is valid. A marked
    /// address receives nothing until the user saves an address again (DEC-50).
    /// </summary>
    public DateTime? ContactEmailRejectedUtc { get; set; }

    /// <summary>Gets or sets the UTC time this record was last changed.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Returns a detached copy with its own <see cref="Categories"/> dictionary.</summary>
    /// <returns>The copy.</returns>
    public UserPreference Clone() => new()
    {
        UserId = UserId,
        ContactEmail = ContactEmail,
        Categories = new Dictionary<EmailCategory, bool>(Categories),
        AnnouncementsOptOut = AnnouncementsOptOut,
        EpisodesOptOut = EpisodesOptOut,
        ContactEmailRejectedUtc = ContactEmailRejectedUtc,
        UpdatedAt = UpdatedAt
    };
}

/// <summary>
/// On-disk shape of <c>preferences.json</c>.
/// </summary>
public sealed class PreferencesFile
{
    /// <summary>
    /// Gets or sets the storage schema version. A plain integer for a possible future one-shot
    /// in-place correction, not a migrations framework. Deleting the plugin data directory is
    /// always a valid reset (a lone corrupt file may leave a <c>.corrupt-*</c> sibling).
    /// </summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets or sets the stored per-user preferences.</summary>
    public List<UserPreference> Users { get; set; } = [];
}
