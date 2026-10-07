using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Runs manual emails as background lots, detached from the HTTP request that started them: closing
/// the browser tab or a proxy timeout does not stop a lot. Lots live in memory only (attachments
/// included) until <see cref="ManualSendQueue.Retention"/> after they finish, and are lost on a
/// restart; the idempotency keys in the sent-keys registry still prevent a duplicate if the same lot
/// is sent again. See <see cref="ManualSendQueue"/>.
/// </summary>
public interface IManualSendQueue
{
    /// <summary>Validates <paramref name="request"/>, resolves its recipients and starts a lot.</summary>
    /// <param name="request">The composed email and its audience.</param>
    /// <returns>The lot as started.</returns>
    /// <exception cref="ArgumentException">The request is not valid to send.</exception>
    ManualBatchSnapshot Start(ManualEmailRequest request);

    /// <summary>Gets the state of a lot.</summary>
    /// <param name="id">The lot id.</param>
    /// <returns>The lot, or null when it is unknown or expired.</returns>
    ManualBatchSnapshot? Get(Guid id);

    /// <summary>Gets the most recently started lot still held.</summary>
    /// <returns>The lot, or null when none is held.</returns>
    ManualBatchSnapshot? Latest();

    /// <summary>Sends again, with the same keys, the recipients of a finished lot whose send failed.</summary>
    /// <param name="id">The lot id.</param>
    /// <returns>The outcome, and the lot when it was found.</returns>
    (ManualRetryOutcome Outcome, ManualBatchSnapshot? Batch) Retry(Guid id);
}

/// <summary>The outcome of <see cref="IManualSendQueue.Retry"/>.</summary>
public enum ManualRetryOutcome
{
    /// <summary>The failed recipients are being sent again.</summary>
    Started,

    /// <summary>The lot is unknown or expired.</summary>
    NotFound,

    /// <summary>The lot is still running.</summary>
    Running,

    /// <summary>The lot has no failed recipient.</summary>
    NothingToRetry
}

/// <summary>A point-in-time view of a manual lot.</summary>
/// <param name="Id">The lot id.</param>
/// <param name="Status">"running" or "done".</param>
/// <param name="Total">Number of recipients with an address.</param>
/// <param name="Sent">Messages the provider accepted.</param>
/// <param name="Failed">Messages that failed.</param>
/// <param name="Deduplicated">Messages already sent under the same key.</param>
/// <param name="SkippedNoEmail">Intended users without a valid contact address.</param>
/// <param name="Details">The latest outcome per attempted recipient, in send order.</param>
/// <param name="StartedUtc">When the lot started.</param>
/// <param name="FinishedUtc">When the lot (or its last retry) finished, or null while running.</param>
public sealed record ManualBatchSnapshot(
    Guid Id,
    string Status,
    int Total,
    int Sent,
    int Failed,
    int Deduplicated,
    int SkippedNoEmail,
    IReadOnlyList<ManualSendDetail> Details,
    DateTime StartedUtc,
    DateTime? FinishedUtc);

/// <inheritdoc cref="IManualSendQueue"/>
public sealed class ManualSendQueue : IManualSendQueue, IDisposable
{
    /// <summary>How long a finished lot stays available.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly IManualEmailService _manual;
    private readonly ILogger<ManualSendQueue> _logger;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<Guid, Batch> _batches = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Initializes a new instance of the <see cref="ManualSendQueue"/> class.</summary>
    /// <param name="manual">The manual email service.</param>
    /// <param name="logger">Logger.</param>
    public ManualSendQueue(IManualEmailService manual, ILogger<ManualSendQueue> logger)
        : this(manual, logger, () => DateTime.UtcNow)
    {
    }

    internal ManualSendQueue(IManualEmailService manual, ILogger<ManualSendQueue> logger, Func<DateTime> now)
    {
        _manual = manual;
        _logger = logger;
        _now = now;
    }

    /// <summary>Gets the task of the run in progress for a lot, for tests.</summary>
    /// <param name="id">The lot id.</param>
    /// <returns>The task, or a completed task when the lot is unknown.</returns>
    internal Task Completion(Guid id) => _batches.TryGetValue(id, out var batch) ? batch.Run : Task.CompletedTask;

    /// <inheritdoc/>
    public ManualBatchSnapshot Start(ManualEmailRequest request)
    {
        Purge();
        var plan = _manual.Plan(request);
        var batch = new Batch(Guid.NewGuid(), plan, _now());
        _batches[batch.Id] = batch;
        Launch(batch, null);
        return batch.Snapshot();
    }

    /// <inheritdoc/>
    public ManualBatchSnapshot? Get(Guid id)
    {
        Purge();
        return _batches.TryGetValue(id, out var batch) ? batch.Snapshot() : null;
    }

    /// <inheritdoc/>
    public ManualBatchSnapshot? Latest()
    {
        Purge();
        return _batches.Values.OrderByDescending(b => b.StartedUtc).FirstOrDefault()?.Snapshot();
    }

    /// <inheritdoc/>
    public (ManualRetryOutcome Outcome, ManualBatchSnapshot? Batch) Retry(Guid id)
    {
        Purge();
        if (!_batches.TryGetValue(id, out var batch))
        {
            return (ManualRetryOutcome.NotFound, null);
        }

        lock (batch)
        {
            if (batch.Running)
            {
                return (ManualRetryOutcome.Running, batch.Snapshot());
            }

            var failed = batch.FailedUserIds();
            if (failed.Count == 0)
            {
                return (ManualRetryOutcome.NothingToRetry, batch.Snapshot());
            }

            batch.Running = true;
            batch.FinishedUtc = null;
            Launch(batch, failed);
        }

        return (ManualRetryOutcome.Started, batch.Snapshot());
    }

    private void Launch(Batch batch, IReadOnlySet<Guid>? only)
    {
        batch.Running = true;
        var token = _cts.Token;
        batch.Run = Task.Run(() => RunAsync(batch, only, token), CancellationToken.None);
    }

    private async Task RunAsync(Batch batch, IReadOnlySet<Guid>? only, CancellationToken cancellationToken)
    {
        try
        {
            await _manual.SendAsync(batch.Plan, batch.Id, only, batch.Record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[EasyNotif] Manual lot {LotId} stopped by the server shutdown.", batch.Id);
        }
        catch (Exception ex)
        {
            // Unsent recipients keep no detail, so they show as not processed rather than sent.
            _logger.LogError(ex, "[EasyNotif] Manual lot {LotId} stopped on an error.", batch.Id);
        }
        finally
        {
            lock (batch)
            {
                batch.Running = false;
                batch.FinishedUtc = _now();
            }
        }
    }

    private void Purge()
    {
        var cutoff = _now() - Retention;
        foreach (var (id, batch) in _batches)
        {
            if (!batch.Running && batch.FinishedUtc is { } finished && finished < cutoff)
            {
                _batches.TryRemove(id, out _);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private sealed class Batch(Guid id, ManualEmailPlan plan, DateTime startedUtc)
    {
        // Latest outcome per recipient; a retry replaces the failed entry in place.
        private readonly List<ManualSendDetail> _details = [];

        public Guid Id { get; } = id;

        public ManualEmailPlan Plan { get; } = plan;

        public DateTime StartedUtc { get; } = startedUtc;

        public DateTime? FinishedUtc { get; set; }

        // True from creation, so a lot is never seen as done before its run starts.
        public bool Running { get; set; } = true;

        public Task Run { get; set; } = Task.CompletedTask;

        public void Record(ManualSendDetail detail)
        {
            lock (this)
            {
                var index = _details.FindIndex(d => d.UserId == detail.UserId);
                if (index >= 0)
                {
                    _details[index] = detail;
                }
                else
                {
                    _details.Add(detail);
                }
            }
        }

        public HashSet<Guid> FailedUserIds()
            => [.. _details.Where(d => d.Status == "failed").Select(d => d.UserId)];

        public ManualBatchSnapshot Snapshot()
        {
            lock (this)
            {
                return new ManualBatchSnapshot(
                    Id,
                    Running ? "running" : "done",
                    Plan.Recipients.Count,
                    _details.Count(d => d.Status == "sent"),
                    _details.Count(d => d.Status == "failed"),
                    _details.Count(d => d.Status == "deduplicated"),
                    Plan.SkippedNoEmail,
                    [.. _details],
                    StartedUtc,
                    FinishedUtc);
            }
        }
    }
}
