namespace PrintLogApi.Services;

/// <summary>
/// Resolves a project's start and finish dates from its manual overrides and its prints.
/// Pure: no EF, no AutoMapper, no I/O. This is the ONLY place these rules live — REST
/// mapping, the grouped feed, and the MCP tools all call in here.
/// </summary>
public static class ProjectDateResolver
{
    public readonly record struct PrintDates(
        DateTimeOffset? StartDate,
        int? PrintTimeInSeconds,
        int? EstimatedPrintTimeInSeconds);

    public static (DateOnly Start, DateOnly? Finish) Resolve(
        DateOnly? startOverride,
        DateOnly? finishOverride,
        DateTime createdDate,
        IEnumerable<PrintDates> prints)
    {
        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;

        foreach (var print in prints)
        {
            if (print.StartDate is not { } printStart)
                continue;

            if (earliest is null || printStart < earliest)
                earliest = printStart;

            var printEnd = AddSecondsSaturating(printStart, ResolveDurationSeconds(print));
            if (latest is null || printEnd > latest)
                latest = printEnd;
        }

        var start = startOverride
            ?? (earliest.HasValue
                ? DateOnly.FromDateTime(earliest.Value.UtcDateTime)
                : DateOnly.FromDateTime(AsUtc(createdDate)));

        var finish = finishOverride
            ?? (latest.HasValue ? DateOnly.FromDateTime(latest.Value.UtcDateTime) : null);

        return (start, finish);
    }

    /// <summary>
    /// Start-only resolution for the grouped feed, which sorts on an instant and never
    /// returns a finish date. A manual override sorts at UTC midnight of that day.
    /// </summary>
    public static DateTimeOffset ResolveStartInstant(
        DateOnly? startOverride,
        DateTimeOffset? earliestPrintStart,
        DateTime createdDate)
    {
        if (startOverride is { } pinned)
            return new DateTimeOffset(DateTime.SpecifyKind(
                pinned.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc));

        return earliestPrintStart ?? new DateTimeOffset(AsUtc(createdDate));
    }

    /// <summary>
    /// Actual print time, else the slicer estimate, else zero. Mirrors the fallback
    /// ProjectProfile already applies to TotalPrintTimeInSeconds.
    /// </summary>
    public static int ResolveDurationSeconds(PrintDates print) =>
        print.PrintTimeInSeconds is > 0 ? print.PrintTimeInSeconds.Value
        : print.EstimatedPrintTimeInSeconds is > 0 ? print.EstimatedPrintTimeInSeconds.Value
        : 0;

    /// <summary>
    /// CreatedDate is a bare DateTime with unspecified Kind on TimestampEntity. Every other
    /// consumer marks it UTC explicitly; skipping that reinterprets it as server-local time.
    /// </summary>
    private static DateTime AsUtc(DateTime createdDate) =>
        DateTime.SpecifyKind(createdDate, DateTimeKind.Utc);

    /// <summary>
    /// Print DTOs bound neither StartDate nor the duration fields, so a far-future start plus
    /// a large duration would throw and turn every read of that project into a 500.
    /// </summary>
    private static DateTimeOffset AddSecondsSaturating(DateTimeOffset value, int seconds)
    {
        if (seconds <= 0)
            return value;

        var remainingSeconds = (DateTimeOffset.MaxValue - value).TotalSeconds;
        return seconds >= remainingSeconds ? DateTimeOffset.MaxValue : value.AddSeconds(seconds);
    }
}
