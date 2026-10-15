using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Tag;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.Services;

public class TagService(PrintLogContext context, ICacheVersionService cacheVersionService) : ITagService
{
    public async Task<List<TagDto>> ListAsync(long userId, CancellationToken ct = default)
    {
        var tags = await context.Tags.AsNoTracking()
            .Where(t => t.CreatedById == userId)
            .Select(t => new TagDto { Id = t.Id, Name = t.Name, Color = t.Color })
            .ToListAsync(ct);

        // Ordered in memory: SQL Server and SQLite collate differently, and the list is bounded
        // by Tag.MaxTagsPerUser.
        return tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<TagStatsResponse> GetStatsAsync(long userId, CancellationToken ct = default)
    {
        var tags = await ListAsync(userId, ct);

        // Both ends are filtered to the user. The service never links another user's print or
        // tag, but a stat is a read, and a read carries its own tenant predicate.
        var links = await context.PrintTags.AsNoTracking()
            .Where(pt => pt.Tag.CreatedById == userId && pt.Print.CreatedById == userId)
            .Select(pt => new { pt.TagId, pt.PrintId })
            .ToListAsync(ct);

        // One row per tagged print, then grouped in memory. Aggregating per tag in SQL would put
        // the per-print filament Sum inside a grouped Sum, which SQL Server refuses (error 8124).
        // The totals follow ProjectProfile: measured where recorded, else the estimate.
        var tagged = context.Prints.Where(p => p.CreatedById == userId && p.PrintTags!.Any());
        var printRows = await tagged.AsNoTracking()
            .Select(p => new
            {
                p.Id,
                Seconds = p.PrintTimeInSeconds.HasValue && p.PrintTimeInSeconds > 0 ? p.PrintTimeInSeconds.Value
                    : p.EstimatedPrintTimeInSeconds.HasValue && p.EstimatedPrintTimeInSeconds > 0 ? p.EstimatedPrintTimeInSeconds.Value
                    : 0,
                FilamentMg = p.FilamentUsage!.Sum(pf => pf.AmountMg.HasValue && pf.AmountMg > 0
                    ? (long)pf.AmountMg.Value
                    : pf.EstimatedAmountMg.HasValue && pf.EstimatedAmountMg > 0
                        ? (long)pf.EstimatedAmountMg.Value : 0L),
            })
            .ToListAsync(ct);
        var printById = printRows.ToDictionary(p => p.Id);

        // The same costing pass the analytics tabs use, so a tag's cost and the Costs tab can
        // never disagree about one print.
        var costs = await AnalyticsCostProjection.Project(context, userId, tagged, ct);
        var costById = costs.Prints.ToDictionary(p => p.PrintId, p => p.Total);

        var linksByTag = links.GroupBy(l => l.TagId).ToDictionary(g => g.Key, g => g.Select(l => l.PrintId).ToList());

        var rows = tags.Select(tag =>
        {
            var printIds = linksByTag.TryGetValue(tag.Id, out var ids)
                ? ids.Where(printById.ContainsKey).ToList()
                : [];
            var costed = costs.RowCapExceeded
                ? []
                : printIds.Select(id => costById.GetValueOrDefault(id)).OfType<decimal>().ToList();

            return new TagStatsDto
            {
                Id = tag.Id,
                Name = tag.Name,
                Color = tag.Color,
                PrintCount = printIds.Count,
                TotalPrintTimeInSeconds = printIds.Sum(id => (long)printById[id].Seconds),
                TotalFilamentWeightMg = printIds.Sum(id => printById[id].FilamentMg),
                TotalCost = costed.Count > 0 ? costed.Sum() : null,
                CostedPrintCount = costed.Count,
            };
        }).ToList();

        return new TagStatsResponse
        {
            Currency = costs.Inputs.UserCurrency,
            CostUnavailable = costs.RowCapExceeded,
            Tags = rows,
        };
    }

    public async Task<TagDto> CreateAsync(long userId, SaveTagDto dto, CancellationToken ct = default)
    {
        var name = RequireName(dto.Name);
        var normalized = Tag.Normalize(name);

        if (await context.Tags.AnyAsync(t => t.CreatedById == userId && t.NormalizedName == normalized, ct))
            throw new TagNameConflictException(name);

        // A soft limit: two creates racing at 199 can both pass and leave 201. It bounds the
        // stats query and the autocomplete, which a tag or two over cannot hurt, so it is not
        // worth a lock on every create.
        if (await context.Tags.CountAsync(t => t.CreatedById == userId, ct) >= Tag.MaxTagsPerUser)
            throw new BadRequestException($"You can have at most {Tag.MaxTagsPerUser} tags.");

        var tag = new Tag
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = normalized,
            Color = NormalizeColor(dto.Color),
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.Tags.Add(tag);
        await SaveOrConflict(name, ct);

        cacheVersionService.InvalidateUserCache(userId);
        return ToDto(tag);
    }

    public async Task<TagDto?> UpdateAsync(long userId, Guid id, SaveTagDto dto, CancellationToken ct = default)
    {
        var tag = await context.Tags.FirstOrDefaultAsync(t => t.Id == id && t.CreatedById == userId, ct);
        if (tag == null)
            return null;

        var name = RequireName(dto.Name);
        var normalized = Tag.Normalize(name);

        if (await context.Tags.AnyAsync(t => t.CreatedById == userId && t.NormalizedName == normalized && t.Id != id, ct))
            throw new TagNameConflictException(name);

        tag.Name = name;
        tag.NormalizedName = normalized;
        tag.Color = NormalizeColor(dto.Color);
        tag.UpdatedById = userId;
        await SaveOrConflict(name, ct);

        // A rename shows on every cached summary row that carries the tag.
        cacheVersionService.InvalidateUserCache(userId);
        return ToDto(tag);
    }

    public async Task<bool> DeleteAsync(long userId, Guid id, CancellationToken ct = default)
    {
        var tag = await context.Tags.FirstOrDefaultAsync(t => t.Id == id && t.CreatedById == userId, ct);
        if (tag == null)
            return false;

        // Explicit, not left to the cascade: the links are what every cached summary shows, and
        // removing them first keeps the delete correct even if a provider skips the FK action.
        await context.PrintTags.Where(pt => pt.TagId == id).ExecuteDeleteAsync(ct);
        context.Tags.Remove(tag);
        await context.SaveChangesAsync(ct);

        cacheVersionService.InvalidateUserCache(userId);
        return true;
    }

    public async Task<List<TagDto>?> SetPrintTagsAsync(
        long userId, long printId, IEnumerable<Guid> tagIds, CancellationToken ct = default)
    {
        // The limit applies to distinct ids: a client repeating one id has asked for one tag.
        var wanted = tagIds.Distinct().ToList();
        if (wanted.Count > Tag.MaxTagsPerPrint)
            throw new BadRequestException($"A print can have at most {Tag.MaxTagsPerPrint} tags.");

        var owned = await context.Tags
            .Where(t => t.CreatedById == userId && wanted.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct);

        // One message for "no such tag" and "someone else's tag", so the endpoint is not an
        // existence oracle for tag ids.
        if (owned.Count != wanted.Count)
            throw new BadRequestException("One or more tags do not exist.");

        bool found;
        try
        {
            // SqlServerRetryingExecutionStrategy forbids a user transaction outside a strategy,
            // so the whole transaction is the retriable unit.
            var strategy = context.Database.CreateExecutionStrategy();
            found = await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await context.Database.BeginTransactionAsync(ct);

                // A no-op UPDATE of the print row, for two things at once. It is the ownership
                // check (0 rows: no such print of yours). And it holds the row's lock until
                // commit, so two requests replacing the same print's tags run one after the
                // other: without it, each could delete the other's old links before either
                // inserted, leaving the union of both sets, past the per-print limit.
                //
                // It writes no value. The print is deliberately NOT loaded and saved: that
                // rewrote every column from this request's snapshot, reverting whatever a
                // connector's /complete wrote meanwhile. Nor does UpdatedDate move: tags are
                // private, and a public print's UpdatedDate is the sitemap's lastmod, which a
                // change no visitor can see should not touch.
                var locked = await context.Prints
                    .Where(p => p.Id == printId && p.CreatedById == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedById, p => p.UpdatedById), ct);
                if (locked == 0)
                    return false;

                await context.PrintTags
                    .Where(pt => pt.PrintId == printId && !wanted.Contains(pt.TagId))
                    .ExecuteDeleteAsync(ct);

                var existing = await context.PrintTags
                    .Where(pt => pt.PrintId == printId)
                    .Select(pt => pt.TagId)
                    .ToListAsync(ct);
                foreach (var tagId in wanted.Except(existing))
                    context.PrintTags.Add(new PrintTag { PrintId = printId, TagId = tagId });

                await context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            });
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            // The row lock makes this unreachable on SQL Server, but a lost race on the primary
            // key is a conflict to retry, never a 500.
            context.ChangeTracker.Clear();
            throw new PrintTagsConflictException(ex);
        }

        if (!found)
            return null;

        cacheVersionService.InvalidateUserCache(userId);
        return await TagsForPrint(printId, ct);
    }

    private async Task<List<TagDto>> TagsForPrint(long printId, CancellationToken ct)
    {
        var byPrint = await LoadForPrints(context, [printId], ct);
        return byPrint.TryGetValue(printId, out var tags) ? tags : [];
    }

    /// <summary>
    /// The tags on each of the given prints, ordered by name. A print with none is absent from
    /// the map. The caller decides whether the viewer may see them: tags are owner-only.
    /// </summary>
    public static async Task<Dictionary<long, List<TagDto>>> LoadForPrints(
        PrintLogContext context, IReadOnlyCollection<long> printIds, CancellationToken ct = default)
    {
        if (printIds.Count == 0)
            return [];

        var rows = await context.PrintTags.AsNoTracking()
            .Where(pt => printIds.Contains(pt.PrintId))
            .Select(pt => new { pt.PrintId, pt.Tag.Id, pt.Tag.Name, pt.Tag.Color })
            .ToListAsync(ct);

        // Ordered in memory so every surface agrees, whatever the provider's collation.
        return rows
            .GroupBy(r => r.PrintId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new TagDto { Id = r.Id, Name = r.Name, Color = r.Color })
                    .ToList());
    }

    private async Task SaveOrConflict(string name, CancellationToken ct)
    {
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            // Lost the race the pre-check cannot close: the unique index is the real guard.
            throw new TagNameConflictException(name, ex);
        }
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new BadRequestException("A tag needs a name.");
        return trimmed;
    }

    private static string? NormalizeColor(string? color) =>
        string.IsNullOrWhiteSpace(color) ? null : color.Trim().ToLowerInvariant();

    private static TagDto ToDto(Tag tag) => new() { Id = tag.Id, Name = tag.Name, Color = tag.Color };
}
