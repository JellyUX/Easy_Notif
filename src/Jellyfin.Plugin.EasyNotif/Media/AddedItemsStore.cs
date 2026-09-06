using System.Threading.Channels;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Records, in real server time, which library items were newly added, so the newsletter can find
/// "new media" by add-time rather than by the item's own <c>DateCreated</c> (which Jellyfin takes
/// from the file system and is unreliable for copied files). Fed by the
/// <c>ILibraryManager.ItemAdded</c> event via <see cref="Inject.StartupService"/>. See
/// <see cref="AddedItemsStore"/>.
/// </summary>
public interface IAddedItemsStore
{
    /// <summary>Queues one item id to be recorded as added "now". Non-blocking (R13).</summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    void RecordAdded(Guid itemId);

    /// <summary>Returns the ids recorded as added on or after <paramref name="sinceUtc"/>.</summary>
    /// <param name="sinceUtc">The window start (UTC).</param>
    /// <returns>The item ids.</returns>
    IReadOnlyCollection<Guid> AddedSince(DateTime sinceUtc);

    /// <summary>Starts the background writer.</summary>
    void Start();

    /// <summary>Stops the background writer and flushes.</summary>
    /// <returns>A task that completes once draining has stopped.</returns>
    Task StopAsync();
}

/// <summary>On-disk shape of <c>added-items.json</c>.</summary>
public sealed class AddedItemsFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the recorded additions, oldest first.</summary>
    public List<AddedItemRecord> Items { get; init; } = [];
}

/// <summary>One recorded library addition.</summary>
/// <param name="Ts">The UTC server time the item was added.</param>
/// <param name="Id">The Jellyfin item id.</param>
public sealed record AddedItemRecord(DateTime Ts, Guid Id);

/// <inheritdoc cref="IAddedItemsStore"/>
public sealed class AddedItemsStore : JsonFileStore<AddedItemsFile>, IAddedItemsStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const int MaxRecords = 20000;

    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _drain;

    /// <summary>Initializes a new instance of the <see cref="AddedItemsStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public AddedItemsStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<AddedItemsStore> logger)
        : this(applicationPaths, fileSystem, logger, () => DateTime.UtcNow, Task.Delay)
    {
    }

    internal AddedItemsStore(
        IApplicationPaths applicationPaths,
        IFileSystem fileSystem,
        ILogger<AddedItemsStore> logger,
        Func<DateTime> now,
        Func<TimeSpan, CancellationToken, Task> delay)
        : base(applicationPaths, fileSystem, logger, "added-items.json")
    {
        _now = now;
        _delay = delay;
    }

    /// <inheritdoc/>
    public void RecordAdded(Guid itemId) => _channel.Writer.TryWrite(itemId);

    /// <inheritdoc/>
    public IReadOnlyCollection<Guid> AddedSince(DateTime sinceUtc)
    {
        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        return Read(file => (IReadOnlyCollection<Guid>)file.Items
            .Where(r => r.Ts >= since)
            .Select(r => r.Id)
            .ToHashSet());
    }

    /// <inheritdoc/>
    public void Start() => _drain ??= Task.Run(() => DrainLoopAsync(_cts.Token));

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_drain is not null)
        {
            try
            {
                await _drain.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task DrainLoopAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Let a burst of ItemAdded events (a library scan) accumulate before one write.
            try
            {
                await _delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            var batch = new List<Guid>();
            while (_channel.Reader.TryRead(out var id))
            {
                batch.Add(id);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            var now = _now();
            var cutoff = now - Retention;
            Mutate(file =>
            {
                file.Items.RemoveAll(r => r.Ts < cutoff);
                foreach (var id in batch)
                {
                    file.Items.Add(new AddedItemRecord(now, id));
                }

                if (file.Items.Count > MaxRecords)
                {
                    file.Items.RemoveRange(0, file.Items.Count - MaxRecords);
                }

                return true;
            });
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}
