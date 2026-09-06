using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.EasyNotif.Scheduling;

/// <summary>
/// The shape of a recurrence. Jellyfin's own <c>TaskTriggerInfoType</c> only offers daily, weekly,
/// interval and startup triggers (Synthese.md section 2.2), so the plugin keeps its own calendar
/// and evaluates it on a fixed tick.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecurrenceKind
{
    /// <summary>Every day at <see cref="RecurrenceSchedule.Time"/>.</summary>
    Daily,

    /// <summary>Every week on <see cref="RecurrenceSchedule.DayOfWeek"/> at <see cref="RecurrenceSchedule.Time"/>.</summary>
    Weekly,

    /// <summary>Every month on <see cref="RecurrenceSchedule.DayOfMonth"/> (clamped to the month length) at <see cref="RecurrenceSchedule.Time"/>.</summary>
    Monthly,

    /// <summary>Every <see cref="RecurrenceSchedule.IntervalDays"/> days, re-anchored on each run, at <see cref="RecurrenceSchedule.Time"/>.</summary>
    EveryNDays
}

/// <summary>
/// An immutable recurrence rule with a local wall-clock time. <see cref="NextRunUtc(DateTime, TimeZoneInfo)"/> resolves the
/// strictly next occurrence in a given time zone, clamps a day-of-month past the month length and
/// handles the two daylight-saving edge cases (a skipped hour and a repeated hour).
/// </summary>
public sealed record RecurrenceSchedule
{
    /// <summary>Gets the recurrence shape.</summary>
    public required RecurrenceKind Kind { get; init; }

    /// <summary>Gets the local wall-clock time of day the run fires at.</summary>
    public required TimeOnly Time { get; init; }

    /// <summary>Gets the day of week for a <see cref="RecurrenceKind.Weekly"/> schedule; null otherwise.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DayOfWeek>))]
    public DayOfWeek? DayOfWeek { get; init; }

    /// <summary>Gets the 1-31 day of month for a <see cref="RecurrenceKind.Monthly"/> schedule; null otherwise.</summary>
    public int? DayOfMonth { get; init; }

    /// <summary>Gets the day interval (>= 1) for an <see cref="RecurrenceKind.EveryNDays"/> schedule; null otherwise.</summary>
    public int? IntervalDays { get; init; }

    /// <summary>Creates a daily schedule.</summary>
    /// <param name="time">The local time of day.</param>
    /// <returns>The schedule.</returns>
    public static RecurrenceSchedule Daily(TimeOnly time)
        => new() { Kind = RecurrenceKind.Daily, Time = time };

    /// <summary>Creates a weekly schedule.</summary>
    /// <param name="dayOfWeek">The day of week.</param>
    /// <param name="time">The local time of day.</param>
    /// <returns>The schedule.</returns>
    public static RecurrenceSchedule Weekly(DayOfWeek dayOfWeek, TimeOnly time)
        => new() { Kind = RecurrenceKind.Weekly, Time = time, DayOfWeek = dayOfWeek };

    /// <summary>Creates a monthly schedule.</summary>
    /// <param name="dayOfMonth">The 1-31 day of month; a value past the month length is clamped.</param>
    /// <param name="time">The local time of day.</param>
    /// <returns>The schedule.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The day of month is outside 1-31.</exception>
    public static RecurrenceSchedule Monthly(int dayOfMonth, TimeOnly time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dayOfMonth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dayOfMonth, 31);
        return new() { Kind = RecurrenceKind.Monthly, Time = time, DayOfMonth = dayOfMonth };
    }

    /// <summary>Creates an every-N-days schedule.</summary>
    /// <param name="intervalDays">The day interval (>= 1).</param>
    /// <param name="time">The local time of day.</param>
    /// <returns>The schedule.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The interval is less than 1.</exception>
    public static RecurrenceSchedule EveryNDays(int intervalDays, TimeOnly time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalDays, 1);
        return new() { Kind = RecurrenceKind.EveryNDays, Time = time, IntervalDays = intervalDays };
    }

    /// <summary>
    /// Resolves an IANA time zone id, falling back to <see cref="TimeZoneInfo.Utc"/> when the id is
    /// blank or unknown on this host. Shared by the dispatch service and the startup service.
    /// </summary>
    /// <param name="ianaId">The IANA id, for example <c>Europe/Paris</c>.</param>
    /// <returns>The resolved time zone, or UTC.</returns>
    public static TimeZoneInfo ResolveTimeZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Returns the strictly next occurrence of this schedule after <paramref name="fromUtc"/>,
    /// expressed in UTC.
    /// </summary>
    /// <param name="fromUtc">The reference instant (UTC). The result is always strictly after it.</param>
    /// <param name="tz">The time zone the wall-clock time is interpreted in.</param>
    /// <returns>The next run instant, in UTC.</returns>
    public DateTime NextRunUtc(DateTime fromUtc, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);

        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), tz);
        var candidate = NextLocalCandidate(fromLocal);

        if (tz.IsInvalidTime(candidate))
        {
            // The wall-clock time was skipped by a spring-forward transition; run at the instant the
            // clocks jump to.
            candidate = candidate.AddHours(1);
        }

        if (tz.IsAmbiguousTime(candidate))
        {
            // The wall-clock time happens twice (a fall-back transition); take the first, earlier UTC
            // occurrence, which uses the larger (daylight) offset.
            var offset = tz.GetAmbiguousTimeOffsets(candidate).Max();
            return DateTime.SpecifyKind(candidate - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tz);
    }

    private DateTime NextLocalCandidate(DateTime fromLocal)
    {
        var timeOfDay = Time.ToTimeSpan();

        switch (Kind)
        {
            case RecurrenceKind.Daily:
            {
                var candidate = fromLocal.Date + timeOfDay;
                while (candidate <= fromLocal)
                {
                    candidate = candidate.AddDays(1);
                }

                return candidate;
            }

            case RecurrenceKind.EveryNDays:
            {
                var step = IntervalDays ?? 1;
                var candidate = fromLocal.Date + timeOfDay;
                while (candidate <= fromLocal)
                {
                    candidate = candidate.AddDays(step);
                }

                return candidate;
            }

            case RecurrenceKind.Weekly:
            {
                var target = DayOfWeek ?? System.DayOfWeek.Monday;
                var candidate = fromLocal.Date + timeOfDay;
                while (candidate <= fromLocal || candidate.DayOfWeek != target)
                {
                    candidate = candidate.AddDays(1);
                }

                return candidate;
            }

            case RecurrenceKind.Monthly:
            {
                var wanted = DayOfMonth ?? 1;
                var candidate = MonthlyCandidate(fromLocal.Year, fromLocal.Month, wanted, timeOfDay);
                if (candidate <= fromLocal)
                {
                    var next = new DateTime(fromLocal.Year, fromLocal.Month, 1).AddMonths(1);
                    candidate = MonthlyCandidate(next.Year, next.Month, wanted, timeOfDay);
                }

                return candidate;
            }

            default:
                throw new InvalidOperationException($"Unknown recurrence kind {Kind}.");
        }
    }

    private static DateTime MonthlyCandidate(int year, int month, int wantedDay, TimeSpan timeOfDay)
    {
        var day = Math.Min(wantedDay, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day) + timeOfDay;
    }
}
