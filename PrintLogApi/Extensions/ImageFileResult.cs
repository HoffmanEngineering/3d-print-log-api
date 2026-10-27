using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Services;

namespace PrintLogApi.Extensions;

/// <summary>
/// Builds the response for the print and project image endpoints: the stored content type when
/// it is a raster image type, otherwise a download, always with headers that keep the browser
/// from reinterpreting the bytes.
/// </summary>
public static class ImageFileResult
{
    public const string ContentSecurityPolicy = "default-src 'none'; sandbox";

    public static IActionResult Create(
        ControllerBase controller, Stream content, string? storedContentType, string? blobName)
    {
        var headers = controller.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = ContentSecurityPolicy;

        var servedType = ImageContentTypes.ResolveServedType(storedContentType, blobName);
        if (servedType is not null)
            return controller.File(content, servedType);

        // Setting a download name is what makes FileStreamResult emit
        // "Content-Disposition: attachment".
        var downloadName = Path.GetFileNameWithoutExtension(blobName ?? string.Empty);
        return controller.File(
            content, ImageContentTypes.OctetStream,
            string.IsNullOrEmpty(downloadName) ? "image" : downloadName);
    }
}
