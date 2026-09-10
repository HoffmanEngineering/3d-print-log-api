using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <summary>
/// CRUD over <see cref="PrinterImage"/>. Every method is owner-only, and ownership is
/// <c>Printer.UserId</c> - never <c>PrinterImage.CreatedById</c>, which records the uploader.
/// A printer or image belonging to another user is reported as missing rather than forbidden,
/// so these endpoints are not an existence oracle.
/// </summary>
public interface IPrinterImageService
{
    Task<PrinterImage> AddImageAsync(long printerId, Stream content, long userId, CancellationToken ct = default);

    Task DeleteImageAsync(long printerId, int imageId, long userId, CancellationToken ct = default);

    Task ReorderImagesAsync(long printerId, IList<int> orderedImageIds, long userId, CancellationToken ct = default);

    Task SetDefaultImageAsync(long printerId, int imageId, long userId, CancellationToken ct = default);
}
