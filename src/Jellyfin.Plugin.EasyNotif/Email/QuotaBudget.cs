using Jellyfin.Plugin.EasyNotif.Models;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Shares the daily quota between the scheduled campaigns and the series notifications (audit
/// PERF-04): while an enabled campaign is due before the next 00:00 UTC, when the daily count
/// resets, <see cref="ReservePercent"/> percent of the daily limit is kept for it and the series emails stop
/// short of it (DEC-36). No reserve when the daily check is off.
/// </summary>
public static class QuotaBudget
{
    /// <summary>The percentage of the daily limit kept for a campaign due today.</summary>
    public const int ReservePercent = 40;

    /// <summary>Returns the number of today's sends reserved for the scheduled campaigns.</summary>
    /// <param name="dailyLimit">The configured daily limit, 0 when the daily check is off.</param>
    /// <param name="campaigns">The campaigns.</param>
    /// <param name="nowUtc">The current time (UTC).</param>
    /// <returns>The reserved count, 0 when no enabled campaign is due before the next 00:00 UTC.</returns>
    public static int Reserve(int dailyLimit, IEnumerable<Campaign> campaigns, DateTime nowUtc)
    {
        if (dailyLimit <= 0)
        {
            return 0;
        }

        var nextMidnight = nowUtc.Date.AddDays(1);
        return campaigns.Any(c => c.Enabled && c.NextRunUtc is DateTime next && next < nextMidnight)
            ? ((dailyLimit * ReservePercent) + 99) / 100
            : 0;
    }
}
