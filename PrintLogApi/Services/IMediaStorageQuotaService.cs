namespace PrintLogApi.Services;

/// <summary>
/// The single definition of "storage used." Enforcement and reported usage both read it, so
/// the number a user is shown is the number they are held to.
/// </summary>
/// <remarks>
/// COUNTED MEDIA CLASSES: print attachments, filament images, printer images.
/// Print images are historically uncounted; fixing that is out of scope, but this is the one
/// place it would be fixed.
/// </remarks>
public interface IMediaStorageQuotaService
{
    Task<long> GetUsedBytesAsync(long userId, CancellationToken ct = default);

    Task EnsureCapacityAsync(long userId, long newBytes, CancellationToken ct = default);
}
