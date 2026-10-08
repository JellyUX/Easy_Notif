using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using Jellyfin.Plugin.EasyNotif.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Scheduling;

/// <summary>
/// Sends the new episodes of followed series, one email per user once their debounce window closes.
/// See <see cref="SeriesNotifier"/>.
/// </summary>
public interface ISeriesNotifier
{
    /// <summary>
    /// Flushes the pending episodes whose window closed. Returns immediately, with no I/O beyond the
    /// in-memory queue, when nothing is pending (R13). Inert without a subscription.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the flush is done.</returns>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISeriesNotifier"/>
/// <remarks>
/// <para>Window, per user, over the followed series: the batch leaves once no new episode arrived for
/// <see cref="QuietPeriod"/> (decision D1), or <see cref="MaxWait"/> after its first episode, so a
/// continuous stream still flushes. With the 15 minute tick, the latency is 30 to 45 minutes.</para>
/// <para>Metadata arrive seconds after <c>ItemAdded</c>: an episode still missing its series or both
/// its number and premiere date holds the user's batch for one tick, then goes out under its fallback
/// key and the "new episode" label (DEC-15).</para>
/// <para>Never announced: a key removed within 24 hours (an upgrade), a key the series channel
/// announced up to the episode's arrival, or a key present in the library under another id.</para>
/// <para>Quota (PERF-04): one email per user and per flush, at most <see cref="MaxPerUserPerDay"/>
/// per user and per UTC day (decision D2, DEC-37), and never into the share of the daily quota
/// reserved for a campaign due today (<see cref="QuotaBudget"/>). What is held back stays pending
/// for a later flush.</para>
/// <para>Retries reuse the campaign backoff through a <c>retry-queue.json</c> entry keyed
/// <see cref="RetryPrefix"/> plus the user id, holding no content: the episodes stay pending until
/// the user is served (DEC-35).</para>
/// </remarks>
public sealed class SeriesNotifier : ISeriesNotifier
{
    /// <summary>The key prefix of the series entries in the retry queue.</summary>
    public const string RetryPrefix = "series:";

    /// <summary>The quiet period after the last episode of a user's batch (decision D1).</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(30);

    /// <summary>The longest a batch waits after its first episode.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(6);

    /// <summary>How far back the startup catch-up looks for episodes missed while the server was down.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(24);

    /// <summary>The most series emails a user receives per UTC day (decision D2).</summary>
    public const int MaxPerUserPerDay = 3;

    private const int CatchUpLimit = 500;

    private readonly ISeriesNotifyStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IAnnouncedStore _announced;
    private readonly IAccessFilter _access;
    private readonly IPreferenceService _preferences;
    private readonly IRetryQueueStore _retries;
    private readonly ICampaignStore _campaigns;
    private readonly IEmailSender _sender;
    private readonly IQuotaGuard _quota;
    private readonly ISendLog _sendLog;
    private readonly IConfigAccessor _config;
    private readonly ISecretStore _secrets;
    private readonly IEasyNotifLog _log;
    private readonly ILogger<SeriesNotifier> _logger;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _caughtUp;

    /// <summary>Initializes a new instance of the <see cref="SeriesNotifier"/> class.</summary>
    /// <param name="store">The series notification store.</param>
    /// <param name="libraryManager">The Jellyfin library manager (read-only use).</param>
    /// <param name="announced">The announced-keys registry.</param>
    /// <param name="access">The per-user access filter.</param>
    /// <param name="preferences">The preference service (contact addresses).</param>
    /// <param name="retries">The retry queue (backoff gate).</param>
    /// <param name="campaigns">The campaign store (mail language of the newsletter).</param>
    /// <param name="sender">The email transport.</param>
    /// <param name="quota">The send quota guard.</param>
    /// <param name="sendLog">The send log.</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="secrets">The transport secret store.</param>
    /// <param name="log">The plugin's dedicated log.</param>
    /// <param name="logger">Logger.</param>
    public SeriesNotifier(
        ISeriesNotifyStore store,
        ILibraryManager libraryManager,
        IAnnouncedStore announced,
        IAccessFilter access,
        IPreferenceService preferences,
        IRetryQueueStore retries,
        ICampaignStore campaigns,
        IEmailSender sender,
        IQuotaGuard quota,
        ISendLog sendLog,
        IConfigAccessor config,
        ISecretStore secrets,
        IEasyNotifLog log,
        ILogger<SeriesNotifier> logger)
        : this(store, libraryManager, announced, access, preferences, retries, campaigns, sender, quota, sendLog, config, secrets, log, logger, () => DateTime.UtcNow)
    {
    }

    internal SeriesNotifier(
        ISeriesNotifyStore store,
        ILibraryManager libraryManager,
        IAnnouncedStore announced,
        IAccessFilter access,
        IPreferenceService preferences,
        IRetryQueueStore retries,
        ICampaignStore campaigns,
        IEmailSender sender,
        IQuotaGuard quota,
        ISendLog sendLog,
        IConfigAccessor config,
        ISecretStore secrets,
        IEasyNotifLog log,
        ILogger<SeriesNotifier> logger,
        Func<DateTime> now)
    {
        _store = store;
        _libraryManager = libraryManager;
        _announced = announced;
        _access = access;
        _preferences = preferences;
        _retries = retries;
        _campaigns = campaigns;
        _sender = sender;
        _quota = quota;
        _sendLog = sendLog;
        _config = config;
        _secrets = secrets;
        _log = log;
        _logger = logger;
        _now = now;
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var now = _now();
            if (!_caughtUp)
            {
                _caughtUp = true;
                CatchUp(now);
            }

            if (!_store.HasPending)
            {
                return;
            }

            await FlushPendingAsync(now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Episodes added while the server was down fire no ItemAdded; their DateCreated is the only trace.
    private void CatchUp(DateTime now)
    {
        if (!_store.HasSubscriptions)
        {
            return;
        }

        var since = now - CatchUpWindow;
        var recent = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = CatchUpLimit,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { Fields = [] }
        });

        // Only what arrived after the series was followed, and not what was already announced: a
        // re-queued episode would restart the user's quiet period on every restart.
        var followedSince = _store.Subscriptions()
            .GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.Min(s => s.Since));
        var candidates = recent
            .OfType<Episode>()
            .Where(e => !e.IsVirtualItem
                && followedSince.TryGetValue(e.SeriesId, out var followed)
                && e.DateCreated >= (followed > since ? followed : since))
            .ToList();
        var announced = _announced.AnnouncedOnOrBefore(AnnouncedStore.SeriesChannel, candidates.Select(MediaKey.For), now);
        var episodes = candidates
            .Where(e => !announced.Contains(MediaKey.For(e)))
            .Select(e => (e.Id, e.SeriesId))
            .ToList();
        var queued = _store.Enqueue(episodes, now);
        _log.Info("series.catch-up", new Dictionary<string, object?>
        {
            ["recent"] = candidates.Count,
            ["alreadyAnnounced"] = candidates.Count - episodes.Count,
            ["queued"] = queued
        });
    }

    private async Task FlushPendingAsync(DateTime now, CancellationToken cancellationToken)
    {
        var pruned = _store.Prune(now);
        var pending = _store.Pending();
        if (pending.Count == 0)
        {
            return;
        }

        var items = _libraryManager
            .GetItemList(new InternalItemsQuery { ItemIds = [.. pending.Select(p => p.ItemId)] })
            .OfType<Episode>()
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var gone = pending.Where(p => !items.ContainsKey(p.ItemId)).Select(p => p.ItemId).ToList();
        _store.Drop(gone);
        var live = pending.Where(p => items.ContainsKey(p.ItemId)).ToList();

        // Who is ready: per user, over the pending episodes of the series they follow and were not
        // served yet. An episode still missing its metadata holds the user's batch for one tick.
        var byUser = _store.Subscriptions()
            .GroupBy(s => s.UserId)
            .Select(g =>
            {
                var series = g.Select(s => s.SeriesId).ToHashSet();
                return (UserId: g.Key, Entries: live.Where(p => series.Contains(p.SeriesId) && !p.Notified.Contains(g.Key)).ToList());
            })
            .Where(u => u.Entries.Count > 0 && IsReady(u.Entries, now) && !IsHeldForRetry(u.UserId, now))
            .ToList();

        var toDefer = byUser
            .SelectMany(u => u.Entries)
            .Where(p => p.Attempts == 0 && !HasMetadata(items[p.ItemId]))
            .Select(p => p.ItemId)
            .ToHashSet();
        _store.Defer(toDefer);
        var ready = byUser.Where(u => !u.Entries.Any(p => toDefer.Contains(p.ItemId))).ToList();

        var excluded = Exclude(ready.SelectMany(u => u.Entries).DistinctBy(p => p.ItemId).ToList(), items, now, out var keys);
        _store.Drop(excluded);

        var stats = new FlushStats { Pruned = pruned + gone.Count, Deferred = toDefer.Count, Excluded = excluded.Count };
        if (ready.Count > 0)
        {
            await SendAsync(ready, excluded, items, keys, now, stats, cancellationToken).ConfigureAwait(false);
        }

        _log.Info("series.flush", new Dictionary<string, object?>
        {
            ["pending"] = pending.Count,
            ["pruned"] = stats.Pruned,
            ["readyUsers"] = ready.Count,
            ["deferred"] = stats.Deferred,
            ["excluded"] = stats.Excluded,
            ["sent"] = stats.Sent,
            ["failed"] = stats.Failed,
            ["retryQueued"] = stats.RetryQueued,
            ["held"] = stats.Held,
            ["capped"] = stats.Capped
        });
    }

    private async Task SendAsync(
        List<(Guid UserId, List<PendingEpisode> Entries)> ready,
        IReadOnlySet<Guid> excluded,
        Dictionary<Guid, Episode> items,
        Dictionary<Guid, string> keys,
        DateTime now,
        FlushStats stats,
        CancellationToken cancellationToken)
    {
        var cfg = _config.Get();
        var secrets = _secrets.Get();
        if (string.IsNullOrWhiteSpace(secrets.ResendApiKey) || string.IsNullOrWhiteSpace(cfg.FromEmail))
        {
            // Not lost: the episodes stay pending until the transport is configured, within 7 days.
            stats.Held = ready.Count;
            _log.Warn("series.skipped", new Dictionary<string, object?> { ["reason"] = "transport-not-configured" });
            return;
        }

        var campaigns = _campaigns.All();
        var french = campaigns.FirstOrDefault(c => c.Type == CampaignType.Newsletter)?.MailLanguage != "en";
        var startOfDay = now.Date;
        for (var i = 0; i < ready.Count; i++)
        {
            var (userId, entries) = ready[i];
            var quota = _quota.Snapshot();
            if (quota.Over)
            {
                // Postponed to a later flush, without loss (section 9.3).
                stats.Held = ready.Count - i;
                _log.Warn("series.skipped", new Dictionary<string, object?> { ["reason"] = "quota-over" });
                return;
            }

            var reserve = QuotaBudget.Reserve(quota.DailyLimit, campaigns, now);
            if (reserve > 0 && quota.DailyToday >= quota.DailyLimit - reserve)
            {
                // The rest of today's quota belongs to the campaign due today; held, not lost.
                stats.Held = ready.Count - i;
                _log.Info("series.skipped", new Dictionary<string, object?>
                {
                    ["reason"] = "quota-reserve",
                    ["dailyToday"] = quota.DailyToday,
                    ["reserve"] = reserve
                });
                return;
            }

            if (_store.SentSince(userId, startOfDay) >= MaxPerUserPerDay)
            {
                // Over the daily cap: the episodes stay pending and join the user's next email.
                stats.Capped++;
                continue;
            }

            var batch = entries.Where(p => !excluded.Contains(p.ItemId)).ToList();
            var visible = _access.VisibleTo(userId, [.. batch.Select(p => p.ItemId)]);
            var hidden = batch.Where(p => !visible.Contains(p.ItemId)).Select(p => p.ItemId).ToList();
            _store.MarkNotified(userId, hidden);
            batch = [.. batch.Where(p => visible.Contains(p.ItemId))];
            if (batch.Count == 0)
            {
                continue;
            }

            var recipients = _preferences.GetContactable([userId]);
            if (recipients.Count == 0)
            {
                // No address: the user is skipped for these episodes, which may then leave the queue.
                _store.MarkNotified(userId, batch.Select(p => p.ItemId));
                continue;
            }

            await SendToUserAsync(recipients[0], batch, items, keys, french, cfg, now, stats, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendToUserAsync(
        Recipient recipient,
        List<PendingEpisode> batch,
        Dictionary<Guid, Episode> items,
        Dictionary<Guid, string> keys,
        bool french,
        PluginConfiguration cfg,
        DateTime now,
        FlushStats stats,
        CancellationToken cancellationToken)
    {
        var batchKeys = batch.Select(p => keys[p.ItemId]).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var (subject, html, text) = SeriesEpisodesRenderer.Render(BuildDigest(batch, items), french);
        var result = await _sender.SendAsync(
            new EmailMessage
            {
                To = recipient.Email,
                Subject = subject,
                Html = html,
                Text = text,
                ReplyTo = cfg.ReplyTo,
                Tags = [new EmailTag("context", "series"), new EmailTag("category", "Series")],
                IdempotencyKey = $"{RetryPrefix}{recipient.UserId:N}:{Hash(batchKeys)}"
            },
            cancellationToken).ConfigureAwait(false);

        var ids = batch.Select(p => p.ItemId).ToList();
        var retryKey = RetryPrefix + recipient.UserId.ToString("N", CultureInfo.InvariantCulture);
        string status;
        if (result.Success || result.Deduplicated)
        {
            if (result.Success)
            {
                _quota.RecordSend();
                _store.RecordSend(recipient.UserId, now);
            }

            status = result.Success ? "sent" : "deduplicated";
            stats.Sent++;
            _store.MarkNotified(recipient.UserId, ids);
            RecordAnnounced(batchKeys, now);
            _retries.Remove(retryKey);
        }
        else if (result.StatusCode is 0 or 429 or >= 500)
        {
            status = "failed";
            QueueRetry(retryKey, recipient.UserId, ids, now, stats);
        }
        else
        {
            // A 4xx will not pass later (DEC-23): the user is skipped for these episodes.
            status = "failed";
            stats.Failed++;
            _store.MarkNotified(recipient.UserId, ids);
            _retries.Remove(retryKey);
        }

        try
        {
            _sendLog.Append(new SendLogEntry
            {
                Ts = now,
                Context = "series",
                Category = "Series",
                ToMasked = EmailMasker.Mask(recipient.Email),
                Subject = subject,
                ResendId = result.ResendId,
                Status = status
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "[EasyNotif] Could not record a series notification in the send log.");
        }
    }

    // The retry entry is only a backoff gate: the content is whatever is still pending for the user.
    private void QueueRetry(string retryKey, Guid userId, List<Guid> ids, DateTime now, FlushStats stats)
    {
        var entry = _retries.Get(retryKey) ?? new RetryEntry
        {
            CampaignId = retryKey,
            KeyPrefix = retryKey,
            RunUtc = now,
            UserIds = [userId],
            FirstFailureUtc = now
        };
        entry.Attempts++;

        if (entry.Attempts > DispatchService.RetryBackoff.Length || now - entry.FirstFailureUtc >= DispatchService.RetryWindow)
        {
            stats.Failed++;
            _log.Warn("series.retry-abandoned", new Dictionary<string, object?> { ["userId"] = userId, ["attempts"] = entry.Attempts, ["episodes"] = ids.Count });
            _store.MarkNotified(userId, ids);
            _retries.Remove(retryKey);
            return;
        }

        stats.RetryQueued++;
        entry.NextAttemptUtc = now + DispatchService.RetryBackoff[entry.Attempts - 1];
        _retries.Upsert(entry);
    }

    // A failed write only loses the re-announce guard for these keys: logged, the send still counts.
    private void RecordAnnounced(IEnumerable<string> keys, DateTime now)
    {
        try
        {
            _announced.Record(AnnouncedStore.SeriesChannel, keys, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error("series.announced", null, ex);
        }
    }

    // Returns the ids never to announce, and the key of every other candidate.
    private HashSet<Guid> Exclude(List<PendingEpisode> candidates, Dictionary<Guid, Episode> items, DateTime now, out Dictionary<Guid, string> keys)
    {
        var keyOf = candidates.ToDictionary(p => p.ItemId, p => MediaKey.For(items[p.ItemId]));
        keys = keyOf;
        var excluded = new HashSet<Guid>();
        if (candidates.Count == 0)
        {
            return excluded;
        }

        var removed = _store.RecentlyRemoved(now);
        var byKey = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var entry in candidates.OrderBy(p => p.FirstSeen))
        {
            var key = keyOf[entry.ItemId];
            if (removed.Contains(key) || !byKey.TryAdd(key, items[entry.ItemId]))
            {
                excluded.Add(entry.ItemId);
            }
        }

        // A key the series channel announced up to the episode's own arrival was another file's.
        foreach (var group in candidates.Where(p => !excluded.Contains(p.ItemId)).GroupBy(p => p.FirstSeen))
        {
            var announced = _announced.AnnouncedOnOrBefore(AnnouncedStore.SeriesChannel, group.Select(p => keyOf[p.ItemId]), group.Key);
            excluded.UnionWith(group.Where(p => announced.Contains(keyOf[p.ItemId])).Select(p => p.ItemId));
        }

        var present = LibraryKeyLookup.PresentOutside(_libraryManager, byKey, items.Keys.ToHashSet());
        excluded.UnionWith(candidates.Where(p => present.Contains(keyOf[p.ItemId])).Select(p => p.ItemId));
        return excluded;
    }

    private SeriesEpisodesDigest BuildDigest(List<PendingEpisode> batch, Dictionary<Guid, Episode> items)
    {
        var groups = batch
            .GroupBy(p => p.SeriesId, p => items[p.ItemId])
            .Select(g => new SeriesEpisodesGroup(
                g.Key,
                SeriesName(g.Key, g.First()),
                [.. g.OrderBy(e => e.ParentIndexNumber ?? int.MaxValue)
                    .ThenBy(e => e.IndexNumber ?? int.MaxValue)
                    .ThenBy(e => e.PremiereDate)
                    .Select(e => new SeriesEpisodeLine(e.ParentIndexNumber, e.IndexNumber, e.IndexNumberEnd, e.IndexNumber is null ? e.PremiereDate : null))]))
            .OrderBy(g => g.SeriesName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new SeriesEpisodesDigest(groups);
    }

    private string SeriesName(Guid seriesId, Episode sample)
    {
        var name = sample.SeriesName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = _libraryManager.GetItemById(seriesId)?.Name;
        }

        return string.IsNullOrWhiteSpace(name) ? "?" : name;
    }

    private bool IsHeldForRetry(Guid userId, DateTime now)
        => _retries.Get(RetryPrefix + userId.ToString("N", CultureInfo.InvariantCulture)) is { } entry && entry.NextAttemptUtc > now;

    private static bool IsReady(List<PendingEpisode> entries, DateTime now)
        => now - entries.Max(p => p.FirstSeen) >= QuietPeriod || now - entries.Min(p => p.FirstSeen) >= MaxWait;

    private static bool HasMetadata(Episode episode)
        => episode.SeriesId != Guid.Empty && (episode.IndexNumber is not null || episode.PremiereDate is not null);

    private static string Hash(IEnumerable<string> sortedKeys)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', sortedKeys)));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private sealed class FlushStats
    {
        public int Pruned { get; set; }

        public int Deferred { get; set; }

        public int Excluded { get; set; }

        public int Sent { get; set; }

        public int Failed { get; set; }

        public int RetryQueued { get; set; }

        public int Held { get; set; }

        public int Capped { get; set; }
    }
}
