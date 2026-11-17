namespace PrintLogApi.Services.ImageBackfill;

/// <summary>Options for one run of <see cref="ImageTransparencyRepairRunner"/>.</summary>
public sealed record TransparencyRepairOptions
{
    /// <summary>False (the default) is a dry run: archive copies are read and processed, nothing is written.</summary>
    public bool Apply { get; init; }

    /// <summary>Stop after this many rows repaired (or, in a dry run, that would be). Null for all.</summary>
    public int? Limit { get; init; }

    /// <summary>Rows and archive names read from earlier backfill logs (<see cref="BackfillLog"/>).</summary>
    public IReadOnlyList<ArchivedOriginal> FromLog { get; init; } = [];
}

/// <summary>An archive copy the repair could not tie to exactly one row, and why.</summary>
public sealed record ArchiveIssue(string ArchivePath, string Reason);

/// <summary>What one transparency repair did, or in a dry run would do.</summary>
public sealed class TransparencyRepairReport
{
    public bool Applied { get; init; }

    /// <summary>Blobs listed in the archive containers.</summary>
    public int ArchiveBlobs { get; set; }

    /// <summary>Archived rows read from <see cref="TransparencyRepairOptions.FromLog"/>.</summary>
    public int LogEntries { get; set; }

    /// <summary>Archive copies tied to exactly one row.</summary>
    public int Matched { get; set; }

    /// <summary>Rows rewritten as a PNG original with a social image on white (or that would be).</summary>
    public int Repaired { get; set; }

    /// <summary>Rows whose archived original has no transparent pixel: their JPEG is right as it is.</summary>
    public int Opaque { get; set; }

    /// <summary>Rows already stored as PNG: repaired by an earlier run, or converted by a fixed backfill.</summary>
    public int AlreadyRepaired { get; set; }

    /// <summary>
    /// Rows with an archive copy but no content type: their conversion never committed, so they
    /// are still legacy rows, and the image backfill (which now keeps transparency) handles them.
    /// </summary>
    public int NotConverted { get; set; }

    /// <summary>Stored bytes of the repaired rows' original and social image, before.</summary>
    public long BytesBefore { get; set; }

    /// <summary>Stored bytes of the repaired rows' original and social image, after.</summary>
    public long BytesAfter { get; set; }

    /// <summary>Archive copies no row could be tied to. Left alone; a human decides.</summary>
    public List<ArchiveIssue> Unmatched { get; } = [];

    /// <summary>Archive copies claimed by more than one row, or rows by more than one copy. Left alone.</summary>
    public List<ArchiveIssue> Conflicts { get; } = [];

    /// <summary>Matched rows the repair did not touch, and why (gone, a size mismatch, an unreadable copy).</summary>
    public List<ImageIssue> Skipped { get; } = [];

    /// <summary>Rows where a step failed. Each was left as it was and can be retried.</summary>
    public List<ImageIssue> Failed { get; } = [];

    /// <summary>Repaired rows whose replaced original or social blob could not be deleted. Harmless.</summary>
    public List<ImageIssue> OldBlobsNotDeleted { get; } = [];

    public void WriteSummary(TextWriter output)
    {
        output.WriteLine(Applied ? "Transparency repair complete" : "DRY RUN - nothing was written (pass --apply to write)");
        output.WriteLine($"  Archive blobs listed  {ArchiveBlobs,10}");
        output.WriteLine($"  Log entries read      {LogEntries,10}");
        output.WriteLine($"  Matched to a row      {Matched,10}");
        output.WriteLine($"  {(Applied ? "Repaired" : "Would repair"),-21} {Repaired,10}");
        output.WriteLine($"  Opaque, left alone    {Opaque,10}");
        output.WriteLine($"  Already PNG           {AlreadyRepaired,10}");
        output.WriteLine($"  Not converted         {NotConverted,10}");
        output.WriteLine($"  Unmatched archives    {Unmatched.Count,10}");
        output.WriteLine($"  Conflicts             {Conflicts.Count,10}");
        output.WriteLine($"  Skipped               {Skipped.Count,10}");
        output.WriteLine($"  Failed                {Failed.Count,10}");
        output.WriteLine($"  Bytes before          {BytesBefore,10}");
        output.WriteLine($"  Bytes after           {BytesAfter,10}");
        if (OldBlobsNotDeleted.Count > 0)
            output.WriteLine($"  Old blobs not deleted {OldBlobsNotDeleted.Count,10}");

        Write(output, "Unmatched archive copies (no row changed; match by hand, see docs/image-backfill.md)",
            Unmatched.Select(i => $"{i.ArchivePath}: {i.Reason}"));
        Write(output, "Conflicts (no row changed)", Conflicts.Select(i => $"{i.ArchivePath}: {i.Reason}"));
        Write(output, "Skipped", Skipped.Select(i => $"{i.Image}: {i.Reason}"));
        Write(output, "Failed (left as they were; re-run to retry)", Failed.Select(i => $"{i.Image}: {i.Reason}"));
        Write(output, "Old blob not deleted", OldBlobsNotDeleted.Select(i => $"{i.Image}: {i.Reason}"));
    }

    private static void Write(TextWriter output, string heading, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        output.WriteLine();
        output.WriteLine($"{heading}:");
        foreach (var line in list)
            output.WriteLine($"  {line}");
    }
}
