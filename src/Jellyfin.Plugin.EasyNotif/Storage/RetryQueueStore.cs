using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Persists the recipients of a campaign run that failed transiently, so the dispatch tick can send
/// them again later with the same idempotency keys. See <see cref="RetryQueueStore"/>.
/// </summary>
public interface IRetryQueueStore
{
    /// <summary>Returns whether at least one entry is due at <paramref name="nowUtc"/>. In memory only.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>True when an entry is due.</returns>
    bool HasDue(DateTime nowUtc);

    /// <summary>Returns detached copies of the entries due at <paramref name="nowUtc"/>.</summary>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>The due entries.</returns>
    IReadOnlyList<RetryEntry> Due(DateTime nowUtc);

    /// <summary>Adds the entry, or replaces the one with the same <see cref="RetryEntry.KeyPrefix"/>.</summary>
    /// <param name="entry">The entry.</param>
    void Upsert(RetryEntry entry);

    /// <summary>Removes the entry with this key prefix, if any.</summary>
    /// <param name="keyPrefix">The entry's <see cref="RetryEntry.KeyPrefix"/>.</param>
    void Remove(string keyPrefix);
}

/// <summary>On-disk shape of <c>retry-queue.json</c>.</summary>
public sealed class RetryQueueFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the pending retries.</summary>
    public List<RetryEntry> Entries { get; init; } = [];
}

/// <summary>The recipients of one campaign run still to be sent.</summary>
public sealed class RetryEntry
{
    /// <summary>Gets or sets the campaign id.</summary>
    public string CampaignId { get; set; } = string.Empty;

    /// <summary>Gets or sets the idempotency key prefix of the run (<c>{campaign}:{slot}</c>); each
    /// recipient's key is this prefix plus <c>:{userId}</c>. Unique per entry.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the instant of the original run, which bounds the content window.</summary>
    public DateTime RunUtc { get; set; }

    /// <summary>Gets or sets the campaign's <c>LastSentUtc</c> before the original run (the newsletter
    /// window start), or null when it had never run.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>Gets or sets the users still to be sent.</summary>
    public List<Guid> UserIds { get; set; } = [];

    /// <summary>Gets or sets the number of attempts made so far, the original run included.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the time of the original failure; the entry is dropped 24 hours later.</summary>
    public DateTime FirstFailureUtc { get; set; }

    /// <summary>Gets or sets the time of the next attempt.</summary>
    public DateTime NextAttemptUtc { get; set; }

    /// <summary>Returns a detached copy.</summary>
    /// <returns>The copy.</returns>
    public RetryEntry Clone()
    {
        var copy = (RetryEntry)MemberwiseClone();
        copy.UserIds = [.. UserIds];
        return copy;
    }
}

/// <inheritdoc cref="IRetryQueueStore"/>
public sealed class RetryQueueStore : JsonFileStore<RetryQueueFile>, IRetryQueueStore
{
    /// <summary>Initializes a new instance of the <see cref="RetryQueueStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public RetryQueueStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<RetryQueueStore> logger)
        : base(applicationPaths, fileSystem, logger, "retry-queue.json")
    {
    }

    /// <inheritdoc/>
    public bool HasDue(DateTime nowUtc) => Read(file => file.Entries.Any(e => e.NextAttemptUtc <= nowUtc));

    /// <inheritdoc/>
    public IReadOnlyList<RetryEntry> Due(DateTime nowUtc)
        => Read(file => (IReadOnlyList<RetryEntry>)[.. file.Entries.Where(e => e.NextAttemptUtc <= nowUtc).Select(e => e.Clone())]);

    /// <inheritdoc/>
    public void Upsert(RetryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var copy = entry.Clone();
        Mutate(file =>
        {
            file.Entries.RemoveAll(e => e.KeyPrefix == copy.KeyPrefix);
            file.Entries.Add(copy);
            return true;
        });
    }

    /// <inheritdoc/>
    public void Remove(string keyPrefix)
        => Mutate(file => file.Entries.RemoveAll(e => e.KeyPrefix == keyPrefix) > 0);
}
