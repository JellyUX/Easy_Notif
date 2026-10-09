using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Playback;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>Reads the health of every background drain for the admin status (OPS-02).</summary>
public interface IDrainStatus
{
    /// <summary>Returns one snapshot per drain, keyed by drain name.</summary>
    /// <returns>The snapshots of <c>newItems</c>, <c>playback</c> and <c>episodes</c>.</returns>
    IReadOnlyDictionary<string, DrainHealthSnapshot> Snapshot();
}

/// <inheritdoc cref="IDrainStatus"/>
public sealed class DrainStatus : IDrainStatus
{
    private readonly IAddedItemsStore _addedItems;
    private readonly IPlaybackHistoryStore _history;
    private readonly ISeriesEpisodeQueue _episodes;

    /// <summary>Initializes a new instance of the <see cref="DrainStatus"/> class.</summary>
    /// <param name="addedItems">The new-items drain.</param>
    /// <param name="history">The playback history drain.</param>
    /// <param name="episodes">The series episode drain.</param>
    public DrainStatus(IAddedItemsStore addedItems, IPlaybackHistoryStore history, ISeriesEpisodeQueue episodes)
    {
        _addedItems = addedItems;
        _history = history;
        _episodes = episodes;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, DrainHealthSnapshot> Snapshot() => new Dictionary<string, DrainHealthSnapshot>
    {
        ["newItems"] = _addedItems.Health.Snapshot(),
        ["playback"] = _history.Health.Snapshot(),
        ["episodes"] = _episodes.Health.Snapshot()
    };
}
