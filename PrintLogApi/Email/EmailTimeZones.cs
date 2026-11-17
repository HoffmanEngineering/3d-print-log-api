using PrintLogApi.Services;

namespace PrintLogApi.Email;

/// <summary>Local-time helpers for scheduling email at a sensible hour in each user's zone.</summary>
public static class EmailTimeZones
{
    /// <summary>
    /// The user's zone, else the configured default, else UTC. <see cref="TimeZoneResolver.ResolveOrUtc"/>
    /// is deliberately not used: it returns UTC before the configured default could apply.
    /// </summary>
    public static TimeZoneInfo For(string? userZoneId, string defaultZoneId)
    {
        if (TimeZoneResolver.TryResolve(userZoneId, out var zone)) return zone;
        if (TimeZoneResolver.TryResolve(defaultZoneId, out var fallback)) return fallback;
        return TimeZoneInfo.Utc;
    }

    /// <summary>
    /// <paramref name="day"/> at <paramref name="hour"/>:00 local. A local time inside a
    /// spring-forward gap does not exist, so it is moved forward one hour; an ambiguous fall-back
    /// time resolves to the standard-time (later) instant.
    /// </summary>
    public static DateTimeOffset AtLocal(DateOnly day, int hour, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToOffset(zone.GetUtcOffset(utc));
    }

    /// <summary>The next <paramref name="hour"/>:00 local at or after <paramref name="now"/>: today if still ahead, else tomorrow.</summary>
    public static DateTimeOffset NextLocalHour(DateTimeOffset now, int hour, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var candidate = AtLocal(today, hour, zone);
        return candidate >= now ? candidate : AtLocal(today.AddDays(1), hour, zone);
    }

    /// <summary>The local calendar date of <paramref name="instant"/> in <paramref name="zone"/>.</summary>
    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
