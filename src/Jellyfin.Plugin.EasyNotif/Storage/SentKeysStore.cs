using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Remembers the idempotency keys of the sends the provider accepted, for
/// <see cref="SentKeysStore.Retention"/>, so a send replayed after the provider's own 24 hour
/// idempotency window (a restart that re-runs an old slot, a retried manual batch) is not delivered
/// twice. See <see cref="SentKeysStore"/>.
/// </summary>
public interface ISentKeysStore
{
    /// <summary>Returns whether <paramref name="key"/> was recorded as sent within the retention.</summary>
    /// <param name="key">The idempotency key.</param>
    /// <returns>True when the key was already sent.</returns>
    bool Contains(string key);

    /// <summary>Records <paramref name="key"/> as sent now, and drops the expired keys.</summary>
    /// <param name="key">The idempotency key.</param>
    void Record(string key);
}

/// <summary>On-disk shape of <c>sent-keys.json</c>.</summary>
public sealed class SentKeysFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the sent idempotency keys and the UTC time each was recorded.</summary>
    public Dictionary<string, DateTime> Keys { get; init; } = new(StringComparer.Ordinal);
}

/// <inheritdoc cref="ISentKeysStore"/>
public sealed class SentKeysStore : JsonFileStore<SentKeysFile>, ISentKeysStore
{
    /// <summary>How long a sent key is remembered.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly Func<DateTime> _now;

    /// <summary>Initializes a new instance of the <see cref="SentKeysStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public SentKeysStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<SentKeysStore> logger)
        : this(applicationPaths, fileSystem, logger, () => DateTime.UtcNow)
    {
    }

    internal SentKeysStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<SentKeysStore> logger, Func<DateTime> now)
        : base(applicationPaths, fileSystem, logger, "sent-keys.json")
    {
        _now = now;
    }

    /// <inheritdoc/>
    public bool Contains(string key)
    {
        var cutoff = _now() - Retention;
        return Read(file => file.Keys.TryGetValue(key, out var ts) && ts >= cutoff);
    }

    /// <inheritdoc/>
    public void Record(string key)
    {
        var now = _now();
        var cutoff = now - Retention;
        Mutate(file =>
        {
            foreach (var expired in file.Keys.Where(k => k.Value < cutoff).Select(k => k.Key).ToList())
            {
                file.Keys.Remove(expired);
            }

            file.Keys[key] = now;
            return true;
        });
    }
}
