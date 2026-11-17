namespace PrintLogApi.Models.DTOs.Sitemap;

/// <summary>
/// One public page for the UI's deploy-time sitemap generator: the entity id it builds the URL
/// from, and when that entity was last modified.
/// </summary>
/// <param name="Id">The id the page URL is built from.</param>
/// <param name="LastModified">
/// The last time the row was saved, in UTC. Null when no trustworthy timestamp exists, so the
/// sitemap omits <c>lastmod</c> rather than publishing an invented date.
/// </param>
public record SitemapEntryDto(long Id, DateTimeOffset? LastModified)
{
    /// <summary>
    /// Anything earlier is an unset column (<see cref="DateTime.MinValue"/> or a SQL default)
    /// rather than a real save, and must not reach a sitemap as a <c>lastmod</c>.
    /// </summary>
    internal static readonly DateTime EarliestPlausible = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Builds an entry from a <see cref="TimestampEntity.UpdatedDate"/>. That column is written as
    /// <see cref="DateTime.UtcNow"/> by <c>PrintLogContext.UpdateTimestamps</c> but reads back with
    /// <see cref="DateTimeKind.Unspecified"/>, so it is marked UTC here rather than serialized
    /// without an offset.
    /// </summary>
    public static SitemapEntryDto FromUpdatedDate(long id, DateTime updatedDate) =>
        new(id, updatedDate < EarliestPlausible
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(updatedDate, DateTimeKind.Utc)));
}
