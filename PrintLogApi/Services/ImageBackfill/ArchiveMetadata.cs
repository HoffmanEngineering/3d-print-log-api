using System.Globalization;

namespace PrintLogApi.Services.ImageBackfill;

/// <summary>
/// Blob metadata on an archive copy that names the image row it came from. The backfill writes
/// it; the transparency repair reads it. Copies archived before it existed have none: see
/// <see cref="ImageTransparencyRepairRunner"/> for how those are matched.
/// </summary>
public static class ArchiveMetadata
{
    /// <summary><see cref="ImageRef.Kind"/>: print-image or project-image.</summary>
    public const string ImageKind = "imagekind";

    /// <summary>The image row's id.</summary>
    public const string ImageId = "imageid";

    /// <summary>The id of the row's original File, which the backfill keeps.</summary>
    public const string FileId = "fileid";

    public static IReadOnlyDictionary<string, string> For(ImageRef image, Guid fileId) => new Dictionary<string, string>
    {
        [ImageKind] = image.Kind,
        [ImageId] = image.Id.ToString(CultureInfo.InvariantCulture),
        [FileId] = fileId.ToString(),
    };

    /// <summary>The row an archive copy's metadata names, or null when it names none of this kind.</summary>
    public static int? ImageIdOf(IReadOnlyDictionary<string, string> metadata, string kind)
        => metadata.TryGetValue(ImageKind, out var k) && k == kind
            && metadata.TryGetValue(ImageId, out var id)
            && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
}
