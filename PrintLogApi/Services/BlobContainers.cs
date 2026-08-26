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
}
