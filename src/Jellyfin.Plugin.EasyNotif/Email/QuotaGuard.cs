using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Counts sends over a rolling 30-day window against the provider's limits (configurable, Resend
/// free tier by default: 3000 over 30 days, 100 a day) and warns the admin at 80 percent
/// (Synthese.md section 3.1). Callers stop sending once <see cref="QuotaSnapshot.Over"/> is set.
/// The provider's own counter is authoritative: a quota rejection from Resend suspends every send
/// until the next 00:00 UTC, even below the local count. The "day" is UTC, like Resend's.
/// </summary>
public interface IQuotaGuard
{
    /// <summary>Records one send at "now" and purges entries older than 30 days.</summary>
    void RecordSend();

    /// <summary>Suspends every send until the next 00:00 UTC, after the provider refused a quota.</summary>
    /// <param name="reason">The provider's error name, for the log.</param>
    void Suspend(string reason);

    /// <summary>Returns the current window counts and threshold flags.</summary>
    /// <returns>The snapshot.</returns>
    QuotaSnapshot Snapshot();
}

/// <summary>The rolling-window send counts and threshold flags.</summary>
/// <param name="Last30d">Sends in the last 30 days.</param>
/// <param name="DailyToday">Sends since 00:00 UTC today.</param>
/// <param name="MonthlyLimit">The configured 30-day limit.</param>
/// <param name="DailyLimit">The configured daily limit, 0 when the daily check is off.</param>
/// <param name="Warn80">True at or above 80 percent of either limit.</param>
/// <param name="Over">True at or above either limit, or while suspended by the provider.</param>
public sealed record QuotaSnapshot(int Last30d, int DailyToday, int MonthlyLimit, int DailyLimit, bool Warn80, bool Over)
{
    /// <summary>Gets the end of a provider suspension (UTC), or null when none is running.</summary>
    public DateTime? SuspendedUntilUtc { get; init; }
}

/// <summary>On-disk shape of <c>quota.json</c>.</summary>
public sealed class QuotaFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets or sets the epoch-millisecond timestamp of every recorded send.</summary>
    public List<long> Sends { get; set; } = [];

    /// <summary>Gets or sets the epoch-millisecond end of a provider suspension, if any.</summary>
    public long? SuspendedUntilMs { get; set; }
}

/// <inheritdoc cref="IQuotaGuard"/>
public sealed class QuotaGuard : JsonFileStore<QuotaFile>, IQuotaGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private readonly IConfigAccessor _config;
    private readonly Func<DateTimeOffset> _now;
    private readonly IEasyNotifLog _easyNotifLog;

    /// <summary>Initializes a new instance of the <see cref="QuotaGuard"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="config">The plugin configuration accessor (the limits).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    public QuotaGuard(IApplicationPaths applicationPaths, IFileSystem fileSystem, IConfigAccessor config, ILogger<QuotaGuard> logger, IEasyNotifLog easyNotifLog)
        : this(applicationPaths, fileSystem, config, logger, easyNotifLog, () => DateTimeOffset.UtcNow)
    {
    }

    internal QuotaGuard(
        IApplicationPaths applicationPaths,
        IFileSystem fileSystem,
        IConfigAccessor config,
        ILogger<QuotaGuard> logger,
        IEasyNotifLog easyNotifLog,
        Func<DateTimeOffset> now)
        : base(applicationPaths, fileSystem, logger, "quota.json")
    {
        _config = config;
        _easyNotifLog = easyNotifLog;
        _now = now;
    }

    /// <inheritdoc/>
    public void RecordSend()
    {
        var nowMs = _now().ToUnixTimeMilliseconds();
        var cutoff = _now().Subtract(Window).ToUnixTimeMilliseconds();
        Mutate(file =>
        {
            file.Sends.RemoveAll(ms => ms < cutoff);
            file.Sends.Add(nowMs);
            return true;
        });

        var snapshot = Snapshot();
        if (snapshot.Warn80)
        {
            _easyNotifLog.Warn("quota.threshold", new Dictionary<string, object?>
            {
                ["last30d"] = snapshot.Last30d,
                ["dailyToday"] = snapshot.DailyToday,
                ["monthlyLimit"] = snapshot.MonthlyLimit,
                ["dailyLimit"] = snapshot.DailyLimit,
                ["over"] = snapshot.Over
            });
        }
    }

    /// <inheritdoc/>
    public void Suspend(string reason)
    {
        var until = new DateTimeOffset(_now().UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        Mutate(file =>
        {
            file.SuspendedUntilMs = until.ToUnixTimeMilliseconds();
            return true;
        });
        _easyNotifLog.Warn("quota.suspended", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["untilUtc"] = until.UtcDateTime
        });
    }

    /// <inheritdoc/>
    public QuotaSnapshot Snapshot()
    {
        var (dailyLimit, monthlyLimit) = Limits();
        var nowMs = _now().ToUnixTimeMilliseconds();
        var cutoff = _now().Subtract(Window).ToUnixTimeMilliseconds();
        var startOfDay = new DateTimeOffset(_now().UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();

        return Read(file =>
        {
            var last30d = file.Sends.Count(ms => ms >= cutoff);
            var today = file.Sends.Count(ms => ms >= startOfDay);
            var dailyOn = dailyLimit > 0;
            var warn80 = last30d >= monthlyLimit * 4 / 5 || (dailyOn && today >= dailyLimit * 4 / 5);
            DateTime? suspendedUntil = file.SuspendedUntilMs is long until && until > nowMs
                ? DateTimeOffset.FromUnixTimeMilliseconds(until).UtcDateTime
                : null;
            var over = last30d >= monthlyLimit || (dailyOn && today >= dailyLimit) || suspendedUntil is not null;
            return new QuotaSnapshot(last30d, today, monthlyLimit, dailyLimit, warn80, over) { SuspendedUntilUtc = suspendedUntil };
        });
    }

    // Out-of-range values (a hand-edited XML) are clamped; without a plugin instance, the defaults.
    private (int Daily, int Monthly) Limits()
    {
        try
        {
            var cfg = _config.Get();
            return (
                Math.Clamp(cfg.QuotaDailyLimit, 0, PluginConfiguration.MaxQuotaDailyLimit),
                Math.Clamp(cfg.QuotaMonthlyLimit, 1, PluginConfiguration.MaxQuotaMonthlyLimit));
        }
        catch (InvalidOperationException)
        {
            return (PluginConfiguration.DefaultQuotaDailyLimit, PluginConfiguration.DefaultQuotaMonthlyLimit);
        }
    }
}
