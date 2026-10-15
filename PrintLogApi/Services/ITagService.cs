using PrintLogApi.Models.DTOs.Tag;

namespace PrintLogApi.Services;

/// <summary>
/// Print tags (UI #7). Every method is scoped to <c>userId</c>: another user's tag or print is
/// indistinguishable from a missing one. Every write invalidates the user's cache version.
/// </summary>
public interface ITagService
{
    /// <summary>The user's tags, ordered by name.</summary>
    Task<List<TagDto>> ListAsync(long userId, CancellationToken ct = default);

    /// <summary>Every one of the user's tags with count, time, filament and cost totals.</summary>
    Task<TagStatsResponse> GetStatsAsync(long userId, CancellationToken ct = default);

    /// <exception cref="Exceptions.TagNameConflictException">The name is taken.</exception>
    /// <exception cref="Exceptions.BadRequestException">The name is blank or the user is at the limit.</exception>
    Task<TagDto> CreateAsync(long userId, SaveTagDto dto, CancellationToken ct = default);

    /// <returns>The updated tag, or null when the user has no such tag.</returns>
    /// <exception cref="Exceptions.TagNameConflictException">The name is taken by another tag.</exception>
    Task<TagDto?> UpdateAsync(long userId, Guid id, SaveTagDto dto, CancellationToken ct = default);

    /// <returns>False when the user has no such tag.</returns>
    Task<bool> DeleteAsync(long userId, Guid id, CancellationToken ct = default);

    /// <summary>Replaces the tags on one of the user's prints.</summary>
    /// <returns>The print's tags afterwards, or null when the user has no such print.</returns>
    /// <exception cref="Exceptions.BadRequestException">A tag id is not one of the user's.</exception>
    Task<List<TagDto>?> SetPrintTagsAsync(long userId, long printId, IEnumerable<Guid> tagIds, CancellationToken ct = default);
}
