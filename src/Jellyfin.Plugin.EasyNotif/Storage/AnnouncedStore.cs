using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Remembers which media keys (<see cref="Media.MediaKey"/>) each channel already announced, for
/// <see cref="AnnouncedStore.Retention"/>, so an item Jellyfin re-creates under a new id is not
/// announced again. See <see cref="AnnouncedStore"/>.
/// </summary>
public interface IAnnouncedStore
{
    /// <summary>Returns the keys of <paramref name="keys"/> the channel announced on or before <paramref name="sinceUtc"/>.</summary>
    /// <param name="channel">The announcing channel (<see cref="AnnouncedStore.NewsletterChannel"/>).</param>
    /// <param name="keys">The candidate keys.</param>
    /// <param name="sinceUtc">The window start: a key announced later belongs to the window being rebuilt.</param>
    /// <returns>The keys already announced.</returns>
    IReadOnlySet<string> AnnouncedOnOrBefore(string channel, IEnumerable<string> keys, DateTime sinceUtc);

    /// <summary>Records <paramref name="keys"/> as announced by the channel at <paramref name="announcedUtc"/>, and drops the expired entries.</summary>
    /// <param name="channel">The announcing channel.</param>
    /// <param name="keys">The announced keys.</param>
    /// <param name="announcedUtc">The run instant.</param>
    void Record(string channel, IEnumerable<string> keys, DateTime announcedUtc);
}

/// <summary>On-disk shape of <c>announced.json</c>.</summary>
public sealed class AnnouncedFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the announced entries.</summary>
    public List<AnnouncedEntry> Entries { get; init; } = [];
}

/// <summary>One announced media key.</summary>
/// <param name="Channel">The announcing channel.</param>
/// <param name="Key">The media key.</param>
/// <param name="Ts">The UTC instant of the run that announced it.</param>
public sealed record AnnouncedEntry(string Channel, string Key, DateTime Ts);

/// <inheritdoc cref="IAnnouncedStore"/>
public sealed class AnnouncedStore : JsonFileStore<AnnouncedFile>, IAnnouncedStore
{
    /// <summary>The newsletter channel.</summary>
    public const string NewsletterChannel = "newsletter";

    /// <summary>The series notification channel (DEC-32): separate, so a per-user notification never hides an episode from the newsletter.</summary>
    public const string SeriesChannel = "series";

    /// <summary>How long an announced key is remembered.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    private readonly Func<DateTime> _now;

    /// <summary>Initializes a new instance of the <see cref="AnnouncedStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public AnnouncedStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<AnnouncedStore> logger)
        : this(applicationPaths, fileSystem, logger, () => DateTime.UtcNow)
    {
    }

    internal AnnouncedStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<AnnouncedStore> logger, Func<DateTime> now)
        : base(applicationPaths, fileSystem, logger, "announced.json")
    {
        _now = now;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> AnnouncedOnOrBefore(string channel, IEnumerable<string> keys, DateTime sinceUtc)
    {
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return wanted;
        }

        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        var cutoff = _now() - Retention;
        return Read(file => (IReadOnlySet<string>)file.Entries
            .Where(e => e.Channel == channel && e.Ts >= cutoff && e.Ts <= since && wanted.Contains(e.Key))
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal));
    }

    /// <inheritdoc/>
    public void Record(string channel, IEnumerable<string> keys, DateTime announcedUtc)
    {
        var batch = keys.Distinct(StringComparer.Ordinal).ToList();
        if (batch.Count == 0)
        {
            return;
        }

        var ts = DateTime.SpecifyKind(announcedUtc, DateTimeKind.Utc);
        var cutoff = _now() - Retention;
        Mutate(file =>
        {
            var fresh = batch.ToHashSet(StringComparer.Ordinal);
            file.Entries.RemoveAll(e => e.Ts < cutoff || (e.Channel == channel && fresh.Contains(e.Key)));
            file.Entries.AddRange(batch.Select(k => new AnnouncedEntry(channel, k, ts)));
            return true;
        });
    }
}
