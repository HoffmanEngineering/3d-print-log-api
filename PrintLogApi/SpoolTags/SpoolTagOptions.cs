namespace PrintLogApi.SpoolTags;

public sealed class SpoolTagOptions
{
    public const string SectionName = "SpoolTags";

    /// <summary>Whether the maintenance service imports the SpoolmanDB catalog. Off by default so no host reaches the network unasked.</summary>
    public bool CatalogImportEnabled { get; set; }

    public string CatalogUrl { get; set; } = "https://donkie.github.io/SpoolmanDB/filaments.json";

    /// <summary>When set, the catalog is read from this file instead of <see cref="CatalogUrl"/> (offline development).</summary>
    public string? CatalogFilePath { get; set; }

    public long MaxCatalogBytes { get; set; } = 50 * 1024 * 1024;

    public int FailureRetentionDays { get; set; } = 90;

    /// <summary>How long one catalog import may run before it is cancelled, so a stalled download cannot hold the import lock.</summary>
    public TimeSpan ImportTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RunInterval { get; set; } = TimeSpan.FromDays(1);
}
