using PrintLogApi.Models;
using static PrintLogApi.Models.Print;
using static PrintLogApi.Models.PrintFilament;

namespace PrintLogApi.Services;

/// <summary>
/// A job starting on a printer, as an integration (the Moonraker notifier, the OctoPrint webhook)
/// reports it. <see cref="ExternalSource"/> and <see cref="ExternalId"/> make a redelivered start
/// return the print the first one created.
/// </summary>
public sealed record PrintStartedEvent
{
    public required long UserId { get; init; }

    /// <summary>The printer the job runs on. Must belong to <see cref="UserId"/>.</summary>
    public required long PrinterId { get; init; }

    public required PrintSource Source { get; init; }

    public required string ExternalSource { get; init; }

    public required string ExternalId { get; init; }

    public required string Title { get; init; }

    public required string FileName { get; init; }

    public byte[]? FileHash { get; init; }

    public required DateTimeOffset StartDate { get; init; }

    public int? EstimatedPrintTimeInSeconds { get; init; }

    /// <summary>Estimated usage rows; each takes the filament loaded in its slot.</summary>
    public required IReadOnlyList<PrintEventUsage> Usage { get; init; }

    /// <summary>A webcam snapshot to attach as the print's first image.</summary>
    public IFormFile? Snapshot { get; init; }
}

/// <summary>One usage row for <see cref="PrintStartedEvent"/>.</summary>
/// <param name="Slot">The printer slot (tool) whose loaded filament the row takes.</param>
public sealed record PrintEventUsage(
    int Slot,
    SourceMeasurement EstimatedSource,
    double? EstimatedLengthInM,
    SourceMeasurement Source,
    double? LengthInM,
    string Notes);

/// <summary>
/// A job ending. The print is the newest one still Printing that matches: by
/// <see cref="FileHash"/> when sent, otherwise by <see cref="FileName"/>, and on
/// <see cref="PrinterId"/> when sent.
/// </summary>
public sealed record PrintFinishedEvent
{
    public required long UserId { get; init; }

    public long? PrinterId { get; init; }

    public byte[]? FileHash { get; init; }

    public string? FileName { get; init; }

    /// <summary>Success or Failed.</summary>
    public required PrintStatus Status { get; init; }

    /// <summary>The duration to record; null records that none is known.</summary>
    public int? PrintTimeInSeconds { get; init; }

    /// <summary>
    /// Filament used, in meters, written onto the first usage row (or a new one). Null leaves
    /// usage alone.
    /// </summary>
    public double? ActualLengthInM { get; init; }

    /// <summary>A webcam snapshot to attach, which becomes the default image.</summary>
    public IFormFile? Snapshot { get; init; }
}

/// <summary>The print a start event created, or the one it replayed.</summary>
public sealed record PrintStartedResult(Print Print, bool WasReplayed);
