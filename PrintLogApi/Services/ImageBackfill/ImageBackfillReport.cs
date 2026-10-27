namespace PrintLogApi.Services.ImageBackfill;

/// <summary>One print or project image row, for the report.</summary>
public readonly record struct ImageRef(string Kind, int Id)
{
    public const string PrintImage = "print-image";
    public const string ProjectImage = "project-image";

    public override string ToString() => $"{Kind} {Id}";
}

/// <summary>An image the backfill did not process, and why.</summary>
public sealed record ImageIssue(ImageRef Image, string Reason);

/// <summary>What one backfill run did, or in a dry run would do.</summary>
public sealed class ImageBackfillReport
{
    public bool Applied { get; init; }

    /// <summary>Rows selected that still needed work when the run reached them.</summary>
    public int Examined { get; set; }

    /// <summary>Rows brought to the canonical shape (or that would be, in a dry run).</summary>
    public int Processed { get; set; }

    /// <summary>Of <see cref="Processed"/>, originals converted from GIF or BMP.</summary>
    public int ConvertedFromLegacyFormat { get; set; }

    /// <summary>
    /// Of <see cref="Processed"/>, rows whose original was already canonical and only gained
    /// derivatives (a thumbnail, the social image, or both).
    /// </summary>
    public int DerivativesOnly { get; set; }

    /// <summary>Stored bytes before, for the processed rows: the original as it was.</summary>
    public long BytesBefore { get; set; }

    /// <summary>Stored bytes after, for the processed rows: the original plus any new derivatives.</summary>
    public long BytesAfter { get; set; }

    /// <summary>Images that could not be decoded. They are left as they are.</summary>
    public List<ImageIssue> Undecodable { get; } = [];

    /// <summary>Rows whose original blob does not exist.</summary>
    public List<ImageIssue> Missing { get; } = [];

    /// <summary>Rows where a step failed. Each was left as it was and can be retried.</summary>
    public List<ImageIssue> Failed { get; } = [];

    /// <summary>
    /// Rows that were converted, but whose old original could not be deleted afterwards. Harmless:
    /// nothing points at it, and the archive holds a copy.
    /// </summary>
    public List<ImageIssue> OldBlobsNotDeleted { get; } = [];

    public void WriteSummary(TextWriter output)
    {
        output.WriteLine(Applied ? "Image backfill complete" : "DRY RUN - nothing was written (pass --apply to write)");
        output.WriteLine($"  Examined              {Examined,10}");
        output.WriteLine($"  {(Applied ? "Processed" : "Would process"),-21} {Processed,10}");
        output.WriteLine($"    from GIF or BMP     {ConvertedFromLegacyFormat,10}");
        output.WriteLine($"    derivatives only    {DerivativesOnly,10}");
        output.WriteLine($"  Skipped, undecodable  {Undecodable.Count,10}");
        output.WriteLine($"  Skipped, blob missing {Missing.Count,10}");
        output.WriteLine($"  Failed                {Failed.Count,10}");
        output.WriteLine($"  Bytes before          {BytesBefore,10}");
        output.WriteLine($"  Bytes after           {BytesAfter,10}");
        if (OldBlobsNotDeleted.Count > 0)
            output.WriteLine($"  Old blobs not deleted {OldBlobsNotDeleted.Count,10}");

        WriteIssues(output, "Undecodable (left as they are)", Undecodable);
        WriteIssues(output, "Blob missing", Missing);
        WriteIssues(output, "Failed (left as they were; re-run to retry)", Failed);
        WriteIssues(output, "Old blob not deleted", OldBlobsNotDeleted);
    }

    private static void WriteIssues(TextWriter output, string heading, List<ImageIssue> issues)
    {
        if (issues.Count == 0) return;
        output.WriteLine();
        output.WriteLine($"{heading}:");
        foreach (var issue in issues)
            output.WriteLine($"  {issue.Image}: {issue.Reason}");
    }
}
