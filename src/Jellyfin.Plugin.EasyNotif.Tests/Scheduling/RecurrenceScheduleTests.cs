using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Scheduling;

/// <summary>
/// Covers <see cref="RecurrenceSchedule.NextRunUtc"/>: the four presets, strict advancement,
/// month-end clamping, year rollover, the every-N-days re-anchor, the two Paris daylight-saving
/// edges and JSON round-tripping.
/// </summary>
public sealed class RecurrenceScheduleTests
{
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");
    private static readonly TimeOnly Nine = new(9, 0);

    private static DateTime Utc(int y, int mo, int d, int h, int mi)
        => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Daily_ReturnsTheNextLocalNineOClock()
    {
        var schedule = RecurrenceSchedule.Daily(Nine);

        // 2026-06-15 12:00 UTC = 14:00 Paris (CEST). Next 09:00 Paris is the 16th = 07:00 UTC.
        var next = schedule.NextRunUtc(Utc(2026, 6, 15, 12, 0), Paris);

        Assert.Equal(Utc(2026, 6, 16, 7, 0), next);
    }

    [Fact]
    public void Daily_BeforeTodaysTime_ReturnsTodayNotTomorrow()
    {
        var schedule = RecurrenceSchedule.Daily(Nine);

        // 2026-06-15 05:00 UTC = 07:00 Paris, before 09:00, so today's 09:00 Paris = 07:00 UTC.
        var next = schedule.NextRunUtc(Utc(2026, 6, 15, 5, 0), Paris);

        Assert.Equal(Utc(2026, 6, 15, 7, 0), next);
    }

    [Fact]
    public void NextRunUtc_DependsOnTheTimeZone()
    {
        var schedule = RecurrenceSchedule.Daily(Nine);
        var from = Utc(2026, 6, 15, 0, 0);

        var paris = schedule.NextRunUtc(from, Paris);
        var utc = schedule.NextRunUtc(from, TimeZoneInfo.Utc);

        Assert.NotEqual(paris, utc);
        Assert.Equal(Utc(2026, 6, 15, 7, 0), paris);
        Assert.Equal(Utc(2026, 6, 15, 9, 0), utc);
    }

    [Fact]
    public void FromExactlyOnDue_Advances()
    {
        var schedule = RecurrenceSchedule.Daily(Nine);

        // Exactly 09:00 Paris (07:00 UTC): the result must be strictly later, i.e. the next day.
        var next = schedule.NextRunUtc(Utc(2026, 6, 15, 7, 0), Paris);

        Assert.Equal(Utc(2026, 6, 16, 7, 0), next);
    }

    [Theory]
    [InlineData(DayOfWeek.Friday, 2026, 9, 4)]
    [InlineData(DayOfWeek.Monday, 2026, 8, 31)]
    public void Weekly_LandsOnTheRequestedDay(DayOfWeek day, int ey, int em, int ed)
    {
        var schedule = RecurrenceSchedule.Weekly(day, Nine);

        var next = schedule.NextRunUtc(Utc(2026, 8, 31, 0, 0), Paris);

        Assert.Equal(day, TimeZoneInfo.ConvertTimeFromUtc(next, Paris).DayOfWeek);
        Assert.Equal(new DateTime(ey, em, ed), TimeZoneInfo.ConvertTimeFromUtc(next, Paris).Date);
    }

    [Fact]
    public void Weekly_WhenTodayIsTheDayButTimePassed_GoesToNextWeek()
    {
        var schedule = RecurrenceSchedule.Weekly(DayOfWeek.Monday, Nine);

        // 2026-08-31 is a Monday. 12:00 UTC = 14:00 Paris, past 09:00, so next Monday 2026-09-07.
        var next = schedule.NextRunUtc(Utc(2026, 8, 31, 12, 0), Paris);

        Assert.Equal(new DateTime(2026, 9, 7), TimeZoneInfo.ConvertTimeFromUtc(next, Paris).Date);
    }

    [Theory]
    [InlineData(2026, 1, 31)] // 31-day month
    [InlineData(2026, 2, 28)] // clamp to 28
    [InlineData(2028, 2, 29)] // clamp to 29 on a leap year
    [InlineData(2026, 4, 30)] // clamp to 30
    public void Monthly_ClampsTheDayToTheMonthLength(int year, int month, int expectedDay)
    {
        var schedule = RecurrenceSchedule.Monthly(31, Nine);

        var next = schedule.NextRunUtc(Utc(year, month, 1, 0, 0), Paris);

        var local = TimeZoneInfo.ConvertTimeFromUtc(next, Paris);
        Assert.Equal(new DateTime(year, month, expectedDay), local.Date);
    }

    [Fact]
    public void Monthly_WhenThisMonthsDayHasPassed_RollsToNextMonth()
    {
        var schedule = RecurrenceSchedule.Monthly(15, Nine);

        var next = schedule.NextRunUtc(Utc(2026, 6, 20, 0, 0), Paris);

        Assert.Equal(new DateTime(2026, 7, 15), TimeZoneInfo.ConvertTimeFromUtc(next, Paris).Date);
    }

    [Fact]
    public void Monthly_RollsOverTheYear()
    {
        var schedule = RecurrenceSchedule.Monthly(10, Nine);

        var next = schedule.NextRunUtc(Utc(2026, 12, 20, 0, 0), Paris);

        Assert.Equal(new DateTime(2027, 1, 10), TimeZoneInfo.ConvertTimeFromUtc(next, Paris).Date);
    }

    [Fact]
    public void Monthly_Day29_February_LeapVersusNonLeap()
    {
        var schedule = RecurrenceSchedule.Monthly(29, Nine);

        var leap = schedule.NextRunUtc(Utc(2028, 2, 1, 0, 0), Paris);
        var nonLeap = schedule.NextRunUtc(Utc(2027, 2, 1, 0, 0), Paris);

        Assert.Equal(new DateTime(2028, 2, 29), TimeZoneInfo.ConvertTimeFromUtc(leap, Paris).Date);
        Assert.Equal(new DateTime(2027, 2, 28), TimeZoneInfo.ConvertTimeFromUtc(nonLeap, Paris).Date);
    }

    [Fact]
    public void Daily_RollsOverTheYear()
    {
        var schedule = RecurrenceSchedule.Daily(Nine);

        // 2026-12-31 12:00 UTC = 13:00 Paris (CET), past 09:00, so next is 2027-01-01.
        var next = schedule.NextRunUtc(Utc(2026, 12, 31, 12, 0), Paris);

        Assert.Equal(new DateTime(2027, 1, 1), TimeZoneInfo.ConvertTimeFromUtc(next, Paris).Date);
    }

    [Fact]
    public void EveryNDays_ReAnchorsOnTheReferenceInstant()
    {
        var schedule = RecurrenceSchedule.EveryNDays(3, Nine);

        var first = schedule.NextRunUtc(Utc(2026, 6, 15, 5, 0), Paris);
        Assert.Equal(new DateTime(2026, 6, 15), TimeZoneInfo.ConvertTimeFromUtc(first, Paris).Date);

        // Feeding the previous result back in advances by exactly the interval.
        var second = schedule.NextRunUtc(first, Paris);
        Assert.Equal(new DateTime(2026, 6, 18), TimeZoneInfo.ConvertTimeFromUtc(second, Paris).Date);
    }

    [Fact]
    public void EveryNDays_One_SoonAfter_FiresTheSameDay()
    {
        // The manual "set to every 1 day at now+3min -> fires in ~3min" case.
        var from = Utc(2026, 6, 15, 6, 0); // 08:00 Paris
        var schedule = RecurrenceSchedule.EveryNDays(1, new TimeOnly(8, 3));

        var next = schedule.NextRunUtc(from, Paris);

        Assert.Equal(Utc(2026, 6, 15, 6, 3), next);
    }

    [Fact]
    public void Dst_SpringForward_SkippedHour_IsPushedForward()
    {
        // 2026-03-29: Paris clocks jump 02:00 -> 03:00. 02:30 local does not exist.
        var schedule = RecurrenceSchedule.Daily(new TimeOnly(2, 30));

        var next = schedule.NextRunUtc(Utc(2026, 3, 28, 12, 0), Paris);

        Assert.Equal(Utc(2026, 3, 29, 1, 30), next);
    }

    [Fact]
    public void Dst_FallBack_AmbiguousHour_TakesTheEarlierUtcOccurrence()
    {
        // 2026-10-25: Paris clocks fall 03:00 -> 02:00. 02:30 local happens twice; take the first.
        var schedule = RecurrenceSchedule.Daily(new TimeOnly(2, 30));

        var next = schedule.NextRunUtc(Utc(2026, 10, 24, 12, 0), Paris);

        Assert.Equal(Utc(2026, 10, 25, 0, 30), next);
    }

    [Fact]
    public void ResolveTimeZone_FallsBackToUtc_OnBlankOrUnknown()
    {
        Assert.Equal(TimeZoneInfo.Utc, RecurrenceSchedule.ResolveTimeZone(null));
        Assert.Equal(TimeZoneInfo.Utc, RecurrenceSchedule.ResolveTimeZone(" "));
        Assert.Equal(TimeZoneInfo.Utc, RecurrenceSchedule.ResolveTimeZone("Mars/Olympus_Mons"));
        Assert.Equal("Europe/Paris", RecurrenceSchedule.ResolveTimeZone("Europe/Paris").Id);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void JsonRoundTrip_PreservesEveryField(RecurrenceSchedule schedule)
    {
        var json = JsonSerializer.Serialize(schedule);
        var back = JsonSerializer.Deserialize<RecurrenceSchedule>(json);

        Assert.Equal(schedule, back);
    }

    [Fact]
    public void Json_WritesEnumsAndTimeAsReadableStrings()
    {
        var json = JsonSerializer.Serialize(RecurrenceSchedule.Weekly(DayOfWeek.Friday, Nine));

        Assert.Contains("\"Weekly\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Friday\"", json, StringComparison.Ordinal);
        Assert.Contains("09:00", json, StringComparison.Ordinal);
    }

    public static TheoryData<RecurrenceSchedule> Presets() =>
    [
        RecurrenceSchedule.Daily(Nine),
        RecurrenceSchedule.Weekly(DayOfWeek.Friday, new TimeOnly(9, 0)),
        RecurrenceSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(8, 0)),
        RecurrenceSchedule.Monthly(15, new TimeOnly(7, 30)),
        RecurrenceSchedule.EveryNDays(3, new TimeOnly(8, 0)),
    ];
}
