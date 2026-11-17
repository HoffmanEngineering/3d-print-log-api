namespace PrintLogApi.Services.ImageBackfill;

/// <summary>Options for one run of <see cref="ImageBackfillRunner"/>.</summary>
public sealed record ImageBackfillOptions
{
    public const int DefaultBatchSize = 100;

    /// <summary>False (the default) is a dry run: images are read and processed, nothing is written.</summary>
    public bool Apply { get; init; }

    /// <summary>Rows read per query.</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>Stop after this many rows that need work, across both image kinds. Null for all.</summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Only this print's images. With neither filter set, every print and project image is
    /// considered; with either set, only the filtered kinds are.
    /// </summary>
    public long? PrintId { get; init; }

    /// <summary>Only this project's images. See <see cref="PrintId"/>.</summary>
    public Guid? ProjectId { get; init; }
}
