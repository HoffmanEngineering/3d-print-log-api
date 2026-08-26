using PrintLogApi.Models;

namespace PrintLogApi.Services;

/// <summary>
/// CRUD over <see cref="FilamentImage"/>. Every method is creator-only: a filament or
/// image belonging to another user is reported as missing rather than forbidden, so the
/// API is not an existence oracle.
/// </summary>
public interface IFilamentImageService
{
    Task<FilamentImage> AddImageAsync(Guid filamentId, Stream content, long userId, CancellationToken ct = default);

    Task DeleteImageAsync(Guid filamentId, int imageId, long userId, CancellationToken ct = default);

    Task ReorderImagesAsync(Guid filamentId, IList<int> orderedImageIds, long userId, CancellationToken ct = default);

    Task SetDefaultImageAsync(Guid filamentId, int imageId, long userId, CancellationToken ct = default);
}
