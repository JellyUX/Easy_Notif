using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Storage;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// In-memory <see cref="ICampaignStore"/> for tests. Holds a mutable list of campaigns and applies
/// <see cref="Update"/> mutations in place, like the real store's write-through behaviour.
/// </summary>
public sealed class FakeCampaignStore : ICampaignStore
{
    /// <summary>Initializes a new instance of the <see cref="FakeCampaignStore"/> class.</summary>
    /// <param name="campaigns">The starting campaigns.</param>
    public FakeCampaignStore(params Campaign[] campaigns) => Campaigns = [.. campaigns];

    /// <summary>Gets the held campaigns.</summary>
    public List<Campaign> Campaigns { get; }

    /// <summary>Gets the number of <see cref="Update"/> calls that matched a campaign.</summary>
    public int UpdateCount { get; private set; }

    /// <inheritdoc/>
    public IReadOnlyList<Campaign> All() => [.. Campaigns];

    /// <inheritdoc/>
    public Campaign? Get(string id) => Campaigns.FirstOrDefault(c => c.Id == id);

    /// <inheritdoc/>
    public bool Update(string id, Action<Campaign> mutate)
    {
        var campaign = Campaigns.FirstOrDefault(c => c.Id == id);
        if (campaign is null)
        {
            return false;
        }

        mutate(campaign);
        UpdateCount++;
        return true;
    }
}
