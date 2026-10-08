using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Models;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="QuotaBudget"/>: 40 percent of the daily limit (rounded up) while an enabled
/// campaign is due before the next 00:00 UTC, overdue included, and nothing otherwise or when the
/// daily check is off.
/// </summary>
public class QuotaBudgetTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    private static Campaign Due(DateTime? next, bool enabled = true) => new() { Id = "c", Enabled = enabled, NextRunUtc = next };

    [Theory]
    [InlineData(100, 40)]
    [InlineData(50, 20)]
    [InlineData(7, 3)]
    [InlineData(1, 1)]
    public void Reserve_IsFortyPercentRoundedUp_WhenACampaignIsDueToday(int dailyLimit, int expected)
    {
        Assert.Equal(expected, QuotaBudget.Reserve(dailyLimit, [Due(Now.AddHours(8))], Now));
    }

    [Fact]
    public void Reserve_CountsAnOverdueCampaign()
    {
        Assert.Equal(40, QuotaBudget.Reserve(100, [Due(Now.AddHours(-2))], Now));
    }

    [Fact]
    public void Reserve_IsZero_ForACampaignDueAfterMidnight_DisabledOrUnscheduled()
    {
        Assert.Equal(0, QuotaBudget.Reserve(100, [Due(new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc))], Now));
        Assert.Equal(0, QuotaBudget.Reserve(100, [Due(Now.AddHours(1), enabled: false)], Now));
        Assert.Equal(0, QuotaBudget.Reserve(100, [Due(null)], Now));
        Assert.Equal(0, QuotaBudget.Reserve(100, [], Now));
    }

    [Fact]
    public void Reserve_IsZero_WhenTheDailyCheckIsOff()
    {
        Assert.Equal(0, QuotaBudget.Reserve(0, [Due(Now.AddHours(1))], Now));
    }
}
