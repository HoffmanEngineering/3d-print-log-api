namespace PrintLogApi.Services;

/// <summary>
/// Blob container names. Load-bearing: existing blobs already live under these,
/// so they must not be renamed. Azure requires lowercase.
/// </summary>
public static class BlobContainers
{
    public const string PrintImages = "printimages";
    public const string ProjectImages = "projectimages";
    public const string FilamentImages = "filamentimages";
    public const string PrinterImages = "printerimages";

    /// <summary>
    /// Private copies of print and project image originals as they were before the image
    /// backfill re-processed them. Kept only until the backfill has been checked, then deleted
    /// (docs/image-backfill.md). Nothing in the API reads them.
    /// </summary>
    public const string PrintImagesArchive = "printimages-archive";

    /// <inheritdoc cref="PrintImagesArchive"/>
    public const string ProjectImagesArchive = "projectimages-archive";
}
