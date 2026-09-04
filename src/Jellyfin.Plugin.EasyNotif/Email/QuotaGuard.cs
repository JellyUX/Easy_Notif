using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Counts sends over a rolling 30-day window against the Resend free tier (3000/month, 100/day) and
/// warns the admin at 80 percent (Synthese.md section 3.1). Advisory in v1: it feeds the status
/// block and a warning log, it does not block a send - that decision belongs to the dispatch engine.
/// The "day" is UTC, to line up with Resend's own counter.
/// </summary>
public interface IQuotaGuard
{
    /// <summary>Records one send at "now" and purges entries older than 30 days.</summary>
    void RecordSend();

    /// <summary>Returns the current window counts and threshold flags.</summary>
    /// <returns>The snapshot.</returns>
    QuotaSnapshot Snapshot();
}

/// <summary>The rolling-window send counts and threshold flags.</summary>
/// <param name="Last30d">Sends in the last 30 days.</param>
/// <param name="DailyToday">Sends since 00:00 UTC today.</param>
/// <param name="MonthlyLimit">The free-tier monthly limit.</param>
/// <param name="DailyLimit">The free-tier daily limit.</param>
/// <param name="Warn80">True at or above 80 percent of either limit.</param>
/// <param name="Over">True at or above either limit.</param>
public sealed record QuotaSnapshot(int Last30d, int DailyToday, int MonthlyLimit, int DailyLimit, bool Warn80, bool Over);

/// <summary>On-disk shape of <c>quota.json</c>.</summary>
public sealed class QuotaFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets or sets the epoch-millisecond timestamp of every recorded send.</summary>
    public List<long> Sends { get; set; } = [];
}

/// <inheritdoc cref="IQuotaGuard"/>
public sealed class QuotaGuard : JsonFileStore<QuotaFile>, IQuotaGuard
{
    private const int MonthlyLimit = 3000;
    private const int DailyLimit = 100;
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private readonly Func<DateTimeOffset> _now;
    private readonly IEasyNotifLog _easyNotifLog;

    /// <summary>Initializes a new instance of the <see cref="QuotaGuard"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    public QuotaGuard(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<QuotaGuard> logger, IEasyNotifLog easyNotifLog)
        : this(applicationPaths, fileSystem, logger, easyNotifLog, () => DateTimeOffset.UtcNow)
    {
    }

    internal QuotaGuard(
        IApplicationPaths applicationPaths,
        IFileSystem fileSystem,
        ILogger<QuotaGuard> logger,
        IEasyNotifLog easyNotifLog,
        Func<DateTimeOffset> now)
        : base(applicationPaths, fileSystem, logger, "quota.json")
    {
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
    public QuotaSnapshot Snapshot()
    {
        var cutoff = _now().Subtract(Window).ToUnixTimeMilliseconds();
        var startOfDay = new DateTimeOffset(_now().UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();

        return Read(file =>
        {
            var last30d = file.Sends.Count(ms => ms >= cutoff);
            var today = file.Sends.Count(ms => ms >= startOfDay);
            var warn80 = last30d >= MonthlyLimit * 4 / 5 || today >= DailyLimit * 4 / 5;
            var over = last30d >= MonthlyLimit || today >= DailyLimit;
            return new QuotaSnapshot(last30d, today, MonthlyLimit, DailyLimit, warn80, over);
        });
    }
}
