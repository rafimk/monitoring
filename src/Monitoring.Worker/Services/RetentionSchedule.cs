namespace Monitoring.Worker.Services;

/// <summary>
/// Calculates nightly occurrences from a local wall-clock time and a time zone. It is pure (no clock, no database, no web
/// host) so the worker alone decides when to run and the maths is unit-testable, including daylight-saving changes.
/// </summary>
public static class RetentionSchedule
{
    /// <summary>UTC instant of the occurrence on a given local date.</summary>
    public static DateTime Occurrence(DateOnly localDate, TimeSpan timeOfDay, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.FromTimeSpan(timeOfDay)), DateTimeKind.Unspecified);

        // Spring forward: the wall-clock time does not exist. Use the first valid instant after the gap.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);

        // Fall back: the time occurs twice. Use the first occurrence (the one with the larger UTC offset).
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    /// <summary>First occurrence strictly after <paramref name="afterUtc"/>.</summary>
    public static DateTime NextAfter(DateTime afterUtc, TimeSpan timeOfDay, TimeZoneInfo zone)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));
        for (var d = -1; d <= 2; d++)
        {
            var occurrence = Occurrence(date.AddDays(d), timeOfDay, zone);
            if (occurrence > afterUtc) return occurrence;
        }
        throw new InvalidOperationException("Could not compute the next retention run");
    }

    /// <summary>Latest occurrence at or before <paramref name="nowUtc"/>.</summary>
    public static DateTime LatestAtOrBefore(DateTime nowUtc, TimeSpan timeOfDay, TimeZoneInfo zone)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        for (var d = 1; d >= -2; d--)
        {
            var occurrence = Occurrence(date.AddDays(d), timeOfDay, zone);
            if (occurrence <= nowUtc) return occurrence;
        }
        throw new InvalidOperationException("Could not compute the latest retention run");
    }
}
