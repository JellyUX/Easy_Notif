using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Read/write access to the scheduled campaigns. See <see cref="CampaignStore"/>.
/// </summary>
public interface ICampaignStore
{
    /// <summary>Returns every campaign.</summary>
    /// <returns>The campaigns.</returns>
    IReadOnlyList<Campaign> All();

    /// <summary>Returns one campaign by id, or null when it does not exist.</summary>
    /// <param name="id">The campaign id.</param>
    /// <returns>The campaign, or null.</returns>
    Campaign? Get(string id);

    /// <summary>Applies a mutation to one campaign and persists it.</summary>
    /// <param name="id">The campaign id.</param>
    /// <param name="mutate">The mutation to apply.</param>
    /// <returns>True when the campaign existed and was updated.</returns>
    bool Update(string id, Action<Campaign> mutate);
}

/// <summary>
/// Persists the scheduled campaigns to <c>{DataPath}/Jellyfin.Plugin.EasyNotif/campaigns.json</c>.
/// The two system campaigns are seeded (disabled) at construction if the file does not already
/// contain them; seeding never overwrites an existing row. Locking, atomic writes, the in-memory
/// cache and corrupt-file recovery come from <see cref="JsonFileStore{T}"/>.
/// </summary>
public sealed class CampaignStore : JsonFileStore<CampaignFile>, ICampaignStore
{
    /// <summary>The id of the newsletter campaign.</summary>
    public const string NewsletterId = "newsletter";

    /// <summary>The id of the weekly-recap campaign.</summary>
    public const string WeeklyRecapId = "weekly-recap";

    /// <summary>Initializes a new instance of the <see cref="CampaignStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction, for testability.</param>
    /// <param name="logger">Logger.</param>
    public CampaignStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<CampaignStore> logger)
        : base(applicationPaths, fileSystem, logger, "campaigns.json")
        => SeedSystemCampaigns();

    /// <inheritdoc/>
    public IReadOnlyList<Campaign> All()
        => Read(file => (IReadOnlyList<Campaign>)[.. file.Campaigns]);

    /// <inheritdoc/>
    public Campaign? Get(string id)
        => Read(file => file.Campaigns.FirstOrDefault(c => c.Id == id));

    /// <inheritdoc/>
    public bool Update(string id, Action<Campaign> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var found = false;
        Mutate(file =>
        {
            var campaign = file.Campaigns.FirstOrDefault(c => c.Id == id);
            if (campaign is null)
            {
                return false;
            }

            mutate(campaign);
            found = true;
            return true;
        });
        return found;
    }

    private void SeedSystemCampaigns()
    {
        Mutate(file =>
        {
            var changed = false;

            if (file.Campaigns.All(c => c.Id != NewsletterId))
            {
                file.Campaigns.Add(new Campaign
                {
                    Id = NewsletterId,
                    Type = CampaignType.Newsletter,
                    Category = EmailCategory.News,
                    TemplateId = "newsletter",
                    Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Friday, new TimeOnly(9, 0)),
                    MailLanguage = "fr",
                    Enabled = false
                });
                changed = true;
            }

            if (file.Campaigns.All(c => c.Id != WeeklyRecapId))
            {
                file.Campaigns.Add(new Campaign
                {
                    Id = WeeklyRecapId,
                    Type = CampaignType.WeeklyRecap,
                    Category = EmailCategory.Recap,
                    TemplateId = "weekly-recap",
                    Schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(8, 0)),
                    MailLanguage = "fr",
                    Enabled = false
                });
                changed = true;
            }

            return changed;
        });
    }
}
