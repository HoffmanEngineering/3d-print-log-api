namespace PrintLogApi.Services;

/// <summary>
/// The raster image types the print and project image endpoints will serve inline, and the
/// canonical blob extension for each type the image processor produces.
/// </summary>
public static class ImageContentTypes
{
    public const string OctetStream = "application/octet-stream";

    /// <summary>
    /// Extension to type, for rows stored before uploads were processed. Deliberately a short
    /// allowlist rather than <c>FileExtensionContentTypeProvider</c>: anything not listed here
    /// is served as a download.
    /// </summary>
    private static readonly Dictionary<string, string> RasterTypesByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
        };

    private static readonly HashSet<string> RasterTypes =
        new(RasterTypesByExtension.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonical blob extension for a type the image processor produces.</summary>
    public static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new ArgumentOutOfRangeException(
            nameof(contentType), contentType, "No canonical extension for this content type."),
    };

    /// <summary>
    /// The type to serve a stored image as. The row's own type wins when it is an allowed raster
    /// type; a row without one falls back to the blob's extension, again allowlisted. Null means
    /// neither is a raster image type, and the bytes must be served as a download.
    /// </summary>
    public static string? ResolveServedType(string? storedContentType, string? blobName)
    {
        if (!string.IsNullOrEmpty(storedContentType))
            return RasterTypes.Contains(storedContentType) ? storedContentType.ToLowerInvariant() : null;

        var extension = Path.GetExtension(blobName ?? string.Empty);
        return RasterTypesByExtension.TryGetValue(extension, out var type) ? type : null;
    }
}
