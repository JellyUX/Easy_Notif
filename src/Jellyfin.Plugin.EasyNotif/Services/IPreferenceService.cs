using Jellyfin.Plugin.EasyNotif.Models;

namespace Jellyfin.Plugin.EasyNotif.Services;

/// <summary>A user who should receive a given category of email.</summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="Email">The validated contact address.</param>
/// <param name="ExcludedLibraries">The libraries left out of the newsletter (FEAT-02), set by
/// <see cref="IPreferenceService.GetRecipients"/>; null means every library.</param>
public readonly record struct Recipient(Guid UserId, string Email, IReadOnlyList<Guid>? ExcludedLibraries = null);

/// <summary>
/// One row of the admin preferences table: a Jellyfin user, whether or not they have set any
/// preference. The email is masked (see <see cref="Util.EmailMasker"/>).
/// </summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="UserName">The Jellyfin user name.</param>
/// <param name="MaskedEmail">The masked contact address, or <c>(none)</c>.</param>
/// <param name="HasEmail">True when the user has a valid contact address.</param>
/// <param name="Categories">The opt-in state for every category (missing entries are false).</param>
/// <param name="UpdatedAt">When the user's preference was last changed, or null if never.</param>
public sealed record AdminPreferenceRow(
    Guid UserId,
    string UserName,
    string MaskedEmail,
    bool HasEmail,
    IReadOnlyDictionary<EmailCategory, bool> Categories,
    DateTime? UpdatedAt);

/// <summary>
/// Business logic for user email preferences. See <see cref="PreferenceService"/>.
/// </summary>
public interface IPreferenceService
{
    /// <summary>
    /// Returns the stored preference for a user, or a transient default (opted out of everything,
    /// no contact address) when the user has never set one. The default is not persisted.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>The user's preference.</returns>
    UserPreference GetOrCreate(Guid userId);

    /// <summary>
    /// Sets or clears a user's contact address. A null or blank value clears it. A non-blank value
    /// that is not a valid address throws <see cref="ArgumentException"/>. Any save lifts a
    /// rejected-address mark, even with the same address (DEC-50).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="email">The address, or null/blank to clear it.</param>
    void SetContactEmail(Guid userId, string? email);

    /// <summary>
    /// Applies a spam complaint (DEC-51): every category off, announcements and new episodes opted
    /// out. Followed series are kept, so the user can turn everything back on.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>True when the user has a preferences row and something changed.</returns>
    bool ApplyComplaint(Guid userId);

    /// <summary>
    /// Marks the user's contact address as rejected (hard bounce or suppression, DEC-49). The
    /// address stays stored but receives nothing until the user saves an address again.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>True when the user has a contact address that was not already marked.</returns>
    bool MarkEmailRejected(Guid userId);

    /// <summary>
    /// Sets the opt-in state for one or more categories, and optionally the announcements and new-episodes opt-outs,
    /// in a single write. Categories not present in the dictionary are left unchanged.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="categories">The categories to change and their new state.</param>
    /// <param name="announcementsOptOut">The new announcements opt-out, or null to leave it unchanged.</param>
    /// <param name="episodesOptOut">The new new-episodes opt-out, or null to leave it unchanged.</param>
    void SetCategories(Guid userId, IReadOnlyDictionary<EmailCategory, bool> categories, bool? announcementsOptOut = null, bool? episodesOptOut = null);

    /// <summary>
    /// Replaces the libraries a user left out of the newsletter (FEAT-02, DEC-74). An empty list
    /// clears the exclusions (every library).
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <param name="excludedLibraries">The excluded library ids.</param>
    void SetExcludedLibraries(Guid userId, IReadOnlyCollection<Guid> excludedLibraries);

    /// <summary>
    /// Returns every user who has opted in to a category and has a valid contact address.
    /// </summary>
    /// <param name="category">The category.</param>
    /// <returns>The eligible recipients.</returns>
    IReadOnlyList<Recipient> GetRecipients(EmailCategory category);

    /// <summary>
    /// Returns every user with a valid contact address, ignoring category preferences. Used for
    /// manual admin emails, which are not subject to opt-in (Synthese.md section 7.1).
    /// </summary>
    /// <param name="userIds">When given, restricts the result to these users; null means all.</param>
    /// <param name="excludeAnnouncementOptOuts">When true, leaves out users who opted out of announcements.</param>
    /// <returns>The contactable recipients.</returns>
    IReadOnlyList<Recipient> GetContactable(IReadOnlyCollection<Guid>? userIds = null, bool excludeAnnouncementOptOuts = false);

    /// <summary>
    /// Returns one row per Jellyfin user for the admin table, including users who have never set a
    /// preference. Contact addresses are masked.
    /// </summary>
    /// <returns>The admin rows.</returns>
    IReadOnlyList<AdminPreferenceRow> GetAllForAdmin();

    /// <summary>
    /// Removes a user's stored preference row entirely. Called when the Jellyfin account is deleted
    /// so no cleartext address or opt-in survives the erasure.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    void Purge(Guid userId);
}
