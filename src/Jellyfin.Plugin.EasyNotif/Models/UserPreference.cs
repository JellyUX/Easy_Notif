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

    /// <summary>Gets or sets the UTC time this record was last changed.</summary>
    public DateTime UpdatedAt { get; set; }
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
