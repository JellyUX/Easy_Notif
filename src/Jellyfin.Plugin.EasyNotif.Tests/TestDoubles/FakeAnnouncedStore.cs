using Jellyfin.Plugin.EasyNotif.Storage;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>In-memory <see cref="IAnnouncedStore"/> with no retention.</summary>
public sealed class FakeAnnouncedStore : IAnnouncedStore
{
    /// <summary>Gets the recorded entries, in order.</summary>
    public List<AnnouncedEntry> Entries { get; } = [];

    /// <inheritdoc/>
    public IReadOnlySet<string> AnnouncedOnOrBefore(string channel, IEnumerable<string> keys, DateTime sinceUtc)
    {
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        return Entries
            .Where(e => e.Channel == channel && e.Ts <= sinceUtc && wanted.Contains(e.Key))
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public void Record(string channel, IEnumerable<string> keys, DateTime announcedUtc)
        => Entries.AddRange(keys.Select(k => new AnnouncedEntry(channel, k, announcedUtc)));
}
