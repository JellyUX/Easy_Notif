using System.Net.Mail;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Util;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.EasyNotif.Services;

/// <summary>
/// Business logic for user email preferences: read/write a user's own preference, resolve the
/// recipients of a category (opted in AND has a valid address), and build the admin table by
/// left-joining every Jellyfin user with the stored preferences.
/// </summary>
public sealed class PreferenceService : IPreferenceService
{
    private readonly IPreferencesStore _store;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreferenceService"/> class.
    /// </summary>
    /// <param name="store">The preferences persistence store.</param>
    /// <param name="userManager">Jellyfin user manager, used only to list users for the admin view.</param>
    public PreferenceService(IPreferencesStore store, IUserManager userManager)
    {
        _store = store;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public UserPreference GetOrCreate(Guid userId)
        => _store.ReadAll().FirstOrDefault(p => p.UserId == userId) ?? new UserPreference { UserId = userId };

    /// <inheritdoc/>
    public void SetContactEmail(Guid userId, string? email)
    {
        var normalized = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (normalized is not null && !MailAddress.TryCreate(normalized, out _))
        {
            throw new ArgumentException("Not a valid email address.", nameof(email));
        }

        _store.Mutate(users =>
        {
            var row = Upsert(users, userId);
            row.ContactEmail = normalized;
            row.ContactEmailRejectedUtc = null;
            row.UpdatedAt = DateTime.UtcNow;
            return true;
        });
    }

    /// <inheritdoc/>
    public bool ApplyComplaint(Guid userId)
    {
        var changed = false;
        _store.Mutate(users =>
        {
            var row = users.FirstOrDefault(p => p.UserId == userId);
            if (row is null)
            {
                return false;
            }

            foreach (var category in EmailCategories.All)
            {
                if (row.Categories.GetValueOrDefault(category))
                {
                    row.Categories[category] = false;
                    changed = true;
                }
            }

            changed |= !row.AnnouncementsOptOut || !row.EpisodesOptOut;
            row.AnnouncementsOptOut = true;
            row.EpisodesOptOut = true;
            if (changed)
            {
                row.UpdatedAt = DateTime.UtcNow;
            }

            return changed;
        });
        return changed;
    }

    /// <inheritdoc/>
    public bool MarkEmailRejected(Guid userId)
    {
        var changed = false;
        _store.Mutate(users =>
        {
            var row = users.FirstOrDefault(p => p.UserId == userId);
            if (row is null || string.IsNullOrEmpty(row.ContactEmail) || row.ContactEmailRejectedUtc is not null)
            {
                return false;
            }

            row.ContactEmailRejectedUtc = DateTime.UtcNow;
            row.UpdatedAt = row.ContactEmailRejectedUtc.Value;
            changed = true;
            return true;
        });
        return changed;
    }

    /// <inheritdoc/>
    public void SetCategories(Guid userId, IReadOnlyDictionary<EmailCategory, bool> categories, bool? announcementsOptOut = null, bool? episodesOptOut = null)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count == 0 && announcementsOptOut is null && episodesOptOut is null)
        {
            return;
        }

        _store.Mutate(users =>
        {
            var row = Upsert(users, userId);
            var changed = false;
            foreach (var (category, value) in categories)
            {
                if (!row.Categories.TryGetValue(category, out var current) || current != value)
                {
                    row.Categories[category] = value;
                    changed = true;
                }
            }

            if (announcementsOptOut is { } optOut && row.AnnouncementsOptOut != optOut)
            {
                row.AnnouncementsOptOut = optOut;
                changed = true;
            }

            if (episodesOptOut is { } episodes && row.EpisodesOptOut != episodes)
            {
                row.EpisodesOptOut = episodes;
                changed = true;
            }

            if (changed)
            {
                row.UpdatedAt = DateTime.UtcNow;
            }

            return changed;
        });
    }

    /// <inheritdoc/>
    public IReadOnlyList<Recipient> GetRecipients(EmailCategory category)
    {
        var live = LiveUserIds();
        return _store.ReadAll()
            .Where(p => live.Contains(p.UserId)
                && p.Categories.GetValueOrDefault(category)
                && p.ContactEmailRejectedUtc is null
                && MailAddress.TryCreate(p.ContactEmail, out _))
            .Select(p => new Recipient(p.UserId, p.ContactEmail!))
            .ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<Recipient> GetContactable(IReadOnlyCollection<Guid>? userIds = null, bool excludeAnnouncementOptOuts = false)
    {
        var live = LiveUserIds();
        return _store.ReadAll()
            .Where(p => live.Contains(p.UserId)
                && (userIds is null || userIds.Contains(p.UserId))
                && !(excludeAnnouncementOptOuts && p.AnnouncementsOptOut)
                && p.ContactEmailRejectedUtc is null
                && MailAddress.TryCreate(p.ContactEmail, out _))
            .Select(p => new Recipient(p.UserId, p.ContactEmail!))
            .ToList();
    }

    /// <inheritdoc/>
    public IReadOnlyList<AdminPreferenceRow> GetAllForAdmin()
    {
        var byId = _store.ReadAll().ToDictionary(p => p.UserId);

        return _userManager.GetUsers()
            .Select(user =>
            {
                byId.TryGetValue(user.Id, out var pref);
                var categories = EmailCategories.All.ToDictionary(
                    c => c,
                    c => pref?.Categories.GetValueOrDefault(c) ?? false);
                var hasEmail = MailAddress.TryCreate(pref?.ContactEmail, out _);

                return new AdminPreferenceRow(
                    user.Id,
                    user.Username,
                    EmailMasker.Mask(pref?.ContactEmail),
                    hasEmail,
                    categories,
                    pref?.UpdatedAt);
            })
            .ToList();
    }

    /// <inheritdoc/>
    public void Purge(Guid userId)
        => _store.Mutate(users => users.RemoveAll(p => p.UserId == userId) > 0);

    // A disabled Jellyfin account keeps its preferences row but receives nothing until re-enabled.
    private HashSet<Guid> LiveUserIds()
        => _userManager.GetUsers()
            .Where(u => !u.HasPermission(PermissionKind.IsDisabled))
            .Select(u => u.Id)
            .ToHashSet();

    private static UserPreference Upsert(List<UserPreference> users, Guid userId)
    {
        var existing = users.FirstOrDefault(p => p.UserId == userId);
        if (existing is not null)
        {
            return existing;
        }

        var row = new UserPreference { UserId = userId };
        users.Add(row);
        return row;
    }
}
