using AutoMapper;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;

namespace PrintLogApi.Services;

public class ProjectService(
    PrintLogContext context,
    IMapper mapper,
    IBlobStorageService blobStorageService,
    ICacheVersionService cacheVersionService) : IProjectService
{
    public async Task<Mcp.McpPage<Mcp.ProjectListItem>> ListProjectsForMcp(
        long userId, int page, int pageSize, string? search, Project.ProjectStatus? status, System.Threading.CancellationToken ct)
    {
        var query = context.Projects.AsNoTracking().Where(p => p.CreatedById == userId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(p =>
                EF.Functions.Like(p.Name, $"%{search}%") ||
                (p.Reference != null && EF.Functions.Like(p.Reference, $"%{search}%")));
        }
        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        var total = await query.CountAsync(ct);
        var pageRows = await query
            .OrderByDescending(p => p.UpdatedDate)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Reference,
                p.Status,
                p.ViewStatus,
                p.CreatedDate,
                p.StartDateOverride,
                p.FinishDateOverride,
            })
            .ToListAsync(ct);

        // This is the one project path whose projection IS translated to SQL, so the resolver
        // cannot be called inside the Select above. Materialize the page first, then fetch the
        // print dates for just those ids — scoped to the page so the endpoint stays
        // page-bounded rather than loading every print in the account.
        var pageIds = pageRows.Select(r => r.Id).ToList();
        var dateRows = pageIds.Count == 0
            ? []
            : await context.Prints
                .Where(pr => pr.ProjectId != null && pageIds.Contains(pr.ProjectId.Value))
                .Select(pr => new
                {
                    pr.ProjectId,
                    pr.StartDate,
                    pr.PrintTimeInSeconds,
                    pr.EstimatedPrintTimeInSeconds,
                })
                .AsNoTracking()
                .ToListAsync(ct);

        var printsByProject = dateRows
            .Where(r => r.ProjectId.HasValue)
            .GroupBy(r => r.ProjectId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new ProjectDateResolver.PrintDates(
                    r.StartDate, r.PrintTimeInSeconds, r.EstimatedPrintTimeInSeconds)).ToList());

        var items = pageRows.Select(r =>
        {
            var (start, finish) = ProjectDateResolver.Resolve(
                r.StartDateOverride,
                r.FinishDateOverride,
                r.CreatedDate,
                printsByProject.TryGetValue(r.Id, out var prints)
                    ? prints
                    : Enumerable.Empty<ProjectDateResolver.PrintDates>());

            return new Mcp.ProjectListItem(
                r.Id, r.Name, r.Reference, r.Status.ToString(), r.ViewStatus.ToString(), start, finish);
        }).ToList();

        var totalPages = pageSize > 0 ? (int)System.Math.Ceiling(total / (double)pageSize) : 0;
        return new Mcp.McpPage<Mcp.ProjectListItem>(items, page, pageSize, total, totalPages);
    }

    public async Task<Mcp.CreateProjectResult> CreateProjectForMcp(
        long userId, string name, string? reference, string? description, string? url,
        Project.ProjectStatus status, Project.ProjectViewStatus viewStatus,
        DateOnly? startDate, DateOnly? finishDate, string? idempotencyKey,
        System.Threading.CancellationToken ct)
    {
        const string toolName = "create_project";

        // Same reason as the REST create path: CreatedDate is not stamped until SaveChanges, so
        // validate against the timestamp the row is about to receive rather than 0001-01-01.
        try
        {
            ValidateProjectDates(startDate, finishDate, DateTime.UtcNow, null);
        }
        catch (BadRequestException ex)
        {
            throw Mcp.McpToolException.InvalidArguments(ex.Message);
        }

        idempotencyKey = RequireIdempotencyKey(idempotencyKey);
        string? fingerprint = null;
        if (idempotencyKey != null)
        {
            fingerprint = Mcp.McpRequestFingerprint.ComputeCreateProject(
                name, reference, description, url, status, viewStatus, startDate, finishDate);
            var replay = await FindIdempotentProject(userId, toolName, idempotencyKey, fingerprint, ct);
            if (replay != null)
            {
                return replay;
            }
        }

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Reference = reference,
            Description = description,
            Url = url,
            Status = status,
            ViewStatus = viewStatus,
            StartDateOverride = startDate,
            FinishDateOverride = finishDate,
            CreatedById = userId,
            UpdatedById = userId,
        };

        if (idempotencyKey == null)
        {
            context.Projects.Add(project);
            await context.SaveChangesAsync(ct);
        }
        else
        {
            try
            {
                await CreateProjectWithIdempotencyRecord(project, userId, idempotencyKey, fingerprint, ct);
            }
            catch (DbUpdateException)
            {
                // Possible unique-index race: another identical call created the project first.
                // Clear the failed Added entities so the recovery query reads committed state
                // only, then replay the winner. No such record means the failure was something
                // else entirely — rethrow rather than reporting it as an idempotency problem.
                context.ChangeTracker.Clear();
                var concurrent = await FindIdempotentProject(userId, toolName, idempotencyKey, fingerprint, ct);
                if (concurrent != null)
                {
                    return concurrent;
                }
                throw;
            }
        }

        cacheVersionService.InvalidateUserCache(userId);
        return new Mcp.CreateProjectResult(await DescribeAsync(project, ct), WasReplayed: false);
    }

    /// <summary>
    /// Creates the project and its idempotency record atomically. Lets DbUpdateException escape:
    /// only the caller can tell a lost unique-index race (replayable) from a genuine write
    /// failure (not), because only it knows the key and fingerprint to look the winner up with.
    /// </summary>
    private async Task CreateProjectWithIdempotencyRecord(
        Project project, long userId, string key, string? fingerprint, System.Threading.CancellationToken ct)
    {
        // SqlServerRetryingExecutionStrategy forbids user-initiated transactions unless they run
        // inside an execution strategy, so the whole tx is the retriable unit.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            using var tx = await context.Database.BeginTransactionAsync(ct);
            context.Projects.Add(project);
            await context.SaveChangesAsync(ct);

            context.McpIdempotencyRecords.Add(
                Mcp.McpIdempotencyRecordFactory.ForProject(userId, key, fingerprint, project.Id));
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    private async Task<Mcp.CreateProjectResult?> FindIdempotentProject(
        long userId, string toolName, string key, string? fingerprint, System.Threading.CancellationToken ct)
    {
        var record = await context.McpIdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.ToolName == toolName && r.IdempotencyKey == key, ct);
        if (record == null)
        {
            return null;
        }

        // A key reused with a DIFFERENT payload is a caller bug, not a retry: replaying would
        // silently discard the new arguments. A null fingerprint is a legacy record with no
        // stored payload to compare, so it replays unconditionally.
        if (record.RequestFingerprint != null && record.RequestFingerprint != fingerprint)
        {
            throw Mcp.McpToolException.Conflict("This idempotency key was already used with different arguments.");
        }

        // Reads only its OWN target field. A record scoped to this tool with no CreatedProjectId
        // is dangling, whatever else it may carry. Ownership is re-checked in the predicate.
        var projectId = record.CreatedProjectId;
        var project = projectId.HasValue
            ? await context.Projects.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId.Value && p.CreatedById == userId, ct)
            : null;
        if (project == null)
        {
            throw Mcp.McpToolException.NotFound("The prior result for this idempotency key no longer exists.");
        }

        return new Mcp.CreateProjectResult(await DescribeAsync(project, ct), WasReplayed: true);
    }

    private static string? RequireIdempotencyKey(string? key)
    {
        if (key == null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            throw Mcp.McpToolException.InvalidArguments("idempotencyKey cannot be blank.");
        }
        // Trim BEFORE the length check: the trimmed value is what gets stored and compared, so
        // that is the value the limit applies to.
        var trimmed = key.Trim();
        Mcp.McpWriteValidation.RequireMaxLength(trimmed, 200, "idempotencyKey");
        return trimmed;
    }

    /// <summary>
    /// Echoes a project including its RESOLVED dates.
    /// </summary>
    /// <remarks>
    /// Async because none of the MCP paths load prints — create builds the entity by hand,
    /// update and replay both query Projects alone. Resolving without them would silently
    /// report the creation-date fallback for a project that has dated prints, so this fetches
    /// the three date columns for one project rather than letting the caller forget.
    /// </remarks>
    private async Task<Mcp.ProjectWriteResult> DescribeAsync(
        Project p, System.Threading.CancellationToken ct)
    {
        var printRows = await context.Prints
            .Where(pr => pr.ProjectId == p.Id)
            .Select(pr => new
            {
                pr.StartDate,
                pr.PrintTimeInSeconds,
                pr.EstimatedPrintTimeInSeconds,
            })
            .AsNoTracking()
            .ToListAsync(ct);

        var (start, finish) = ProjectDateResolver.Resolve(
            p.StartDateOverride,
            p.FinishDateOverride,
            p.CreatedDate,
            printRows.Select(r => new ProjectDateResolver.PrintDates(
                r.StartDate, r.PrintTimeInSeconds, r.EstimatedPrintTimeInSeconds)));

        return new Mcp.ProjectWriteResult(
            p.Id, p.Name, p.Reference, p.Description, p.Url,
            p.Status.ToString(), p.ViewStatus.ToString(), start, finish);
    }

    public async Task<Mcp.ProjectWriteResult> UpdateProjectForMcp(
        long userId, Guid id, string? name, string? reference, string? description, string? url,
        Project.ProjectStatus? status, Project.ProjectViewStatus? viewStatus,
        DateOnly? startDate, DateOnly? finishDate, bool clearStartDate, bool clearFinishDate,
        System.Threading.CancellationToken ct)
    {
        // Include the prints: the validation below and the echo both need them, and this tool
        // is the one place a caller can pin a finish date without ever seeing the derived start.
        var project = await context.Projects
            .Include(p => p.Prints!)
            .FirstOrDefaultAsync(p => p.Id == id && p.CreatedById == userId, ct);
        if (project == null)
        {
            throw Mcp.McpToolException.NotFound("Project not found.");
        }
        if (name != null) project.Name = name;
        if (reference != null) project.Reference = reference;
        if (description != null) project.Description = description;
        if (url != null) project.Url = url;
        if (status.HasValue) project.Status = status.Value;
        if (viewStatus.HasValue) project.ViewStatus = viewStatus.Value;

        // Patch-style: a null date means "leave alone". Clearing is its own explicit channel,
        // which is why the tool rejects a date and its clear flag arriving together.
        if (clearStartDate) project.StartDateOverride = null;
        else if (startDate.HasValue) project.StartDateOverride = startDate;

        if (clearFinishDate) project.FinishDateOverride = null;
        else if (finishDate.HasValue) project.FinishDateOverride = finishDate;

        try
        {
            ValidateProjectDates(
                project.StartDateOverride, project.FinishDateOverride, project.CreatedDate, project.Prints);
        }
        catch (BadRequestException ex)
        {
            throw Mcp.McpToolException.InvalidArguments(ex.Message);
        }

        project.UpdatedById = userId;
        await context.SaveChangesAsync(ct);
        cacheVersionService.InvalidateUserCache(userId);
        return await DescribeAsync(project, ct);
    }

    public async Task<PagedList<ProjectSummaryDto>> GetProjectSummariesAsync(
        int pageNumber, int pageSize, long userId,
        string? search = null, Project.ProjectStatus? status = null, string sortBy = "updatedDate")
    {
        IQueryable<Project> query = context.Projects
            .Where(p => p.CreatedById == userId)
            .Include(p => p.Images!)
            .Include(p => p.Prints!)
                .ThenInclude(pr => pr.FilamentUsage!)
            .AsSplitQuery()
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            query = query.Where(p => p.Name!.Contains(trimmed) || p.Reference!.Contains(trimmed));
        }

        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        var orderedQuery = sortBy == "createdDate"
            ? query.OrderByDescending(p => p.CreatedDate)
            : query.OrderByDescending(p => p.UpdatedDate);

        var total = await query.CountAsync();
        var items = await orderedQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var dtos = items.Select(p => mapper.Map<ProjectSummaryDto>(p)).ToList();
        return new PagedList<ProjectSummaryDto>(dtos, total, pageNumber, pageSize);
    }

    public async Task<Project?> GetProjectByIdAsync(Guid id)
    {
        return await context.Projects
            .Include(p => p.Images!)
                .ThenInclude(i => i.File)
            .Include(p => p.Prints!)
                .ThenInclude(pr => pr.FilamentUsage!)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<Project> CreateProjectAsync(AddProjectDto dto, long userId)
    {
        // DateTime.UtcNow, not project.CreatedDate: CreatedDate is stamped by the SaveChanges
        // override in PrintLogContext, so before the save it is still default(DateTime) —
        // 0001-01-01. Validating against that would let every past-dated finish override
        // through, and the row would be inverted the instant it was persisted.
        // Validating before Add/SaveChanges also means a rejected create writes nothing.
        ValidateProjectDates(dto.StartDateOverride, dto.FinishDateOverride, DateTime.UtcNow, null);

        var project = mapper.Map<Project>(dto);
        project.Id = Guid.NewGuid();
        project.CreatedById = userId;
        project.UpdatedById = userId;

        context.Projects.Add(project);
        await context.SaveChangesAsync();

        // Null-forgiven: the project was just persisted, so the re-read always finds it.
        return (await GetProjectByIdAsync(project.Id))!;
    }

    public async Task<Project> UpdateProjectAsync(Guid id, PutProjectDto dto, long userId)
    {
        // Include the prints on the EXISTING query rather than adding a second one: the
        // validation below needs them, and without the include project.Prints is null, so a
        // finish override before the derived start would silently pass.
        var project = await context.Projects
            .Include(p => p.Prints!)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (project == null)
            throw new DoesNotExistException();

        ValidateProjectDates(
            dto.StartDateOverride, dto.FinishDateOverride, project.CreatedDate, project.Prints);

        mapper.Map(dto, project);
        project.UpdatedById = userId;

        await context.SaveChangesAsync();
        // Null-forgiven: loaded and updated above, so the re-read always finds it.
        return (await GetProjectByIdAsync(id))!;
    }

    public async Task DeleteProjectAsync(Guid id, bool deletePrints, long userId)
    {
        var project = await context.Projects
            .Include(p => p.Prints!)
            .Include(p => p.Images!)
                .ThenInclude(img => img.File)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (project == null)
            throw new DoesNotExistException();

        if (deletePrints)
        {
            context.Prints.RemoveRange(project.Prints!);
        }
        else
        {
            foreach (var print in project.Prints!)
            {
                print.ProjectId = null;
            }
        }

        foreach (var image in project.Images!)
        {
            if (image.File != null)
                await blobStorageService.DeleteBlobAsync(BlobContainers.ProjectImages, Path.GetFileName(image.File.Path!));
        }

        context.ProjectImages.RemoveRange(project.Images!);
        context.Projects.Remove(project);
        await context.SaveChangesAsync();
    }

    public async Task<ProjectImage> AddImageAsync(Guid projectId, IFormFile file, long userId)
    {
        var project = await context.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) throw new DoesNotExistException();

        var blobName = $"{Guid.NewGuid()}{System.IO.Path.GetExtension(file.FileName)}";
        using var stream = file.OpenReadStream();
        await blobStorageService.UploadAsync(BlobContainers.ProjectImages, blobName, stream);

        var fileEntity = new Models.File { Path = blobName, Size = file.Length, CreatedById = userId, UpdatedById = userId };
        context.Files.Add(fileEntity);
        await context.SaveChangesAsync();

        var existingCount = await context.ProjectImages.CountAsync(pi => pi.ProjectId == projectId);
        var image = new ProjectImage
        {
            ProjectId = projectId,
            FileId = fileEntity.Id,
            IsDefault = existingCount == 0,
            DisplayOrder = existingCount,
            CreatedById = userId,
            UpdatedById = userId
        };
        context.ProjectImages.Add(image);
        await context.SaveChangesAsync();
        return image;
    }

    public async Task DeleteImageAsync(Guid projectId, int imageId, long userId)
    {
        var image = await context.ProjectImages
            .Include(pi => pi.File)
            .FirstOrDefaultAsync(pi => pi.ProjectId == projectId && pi.Id == imageId);
        if (image == null) throw new DoesNotExistException();

        if (image.File != null)
            await blobStorageService.DeleteBlobAsync(BlobContainers.ProjectImages, Path.GetFileName(image.File.Path!));

        context.ProjectImages.Remove(image);
        await context.SaveChangesAsync();
    }

    public async Task ReorderImagesAsync(Guid projectId, IList<int> orderedImageIds, long userId)
    {
        var images = await context.ProjectImages
            .Where(pi => pi.ProjectId == projectId)
            .ToListAsync();

        for (int i = 0; i < orderedImageIds.Count; i++)
        {
            var img = images.FirstOrDefault(im => im.Id == orderedImageIds[i]);
            if (img != null) img.DisplayOrder = i;
        }
        await context.SaveChangesAsync();
    }

    public async Task SetDefaultImageAsync(Guid projectId, int imageId, long userId)
    {
        var images = await context.ProjectImages
            .Where(pi => pi.ProjectId == projectId)
            .ToListAsync();

        if (!images.Any(img => img.Id == imageId))
            throw new DoesNotExistException();

        foreach (var img in images)
            img.IsDefault = img.Id == imageId;

        await context.SaveChangesAsync();
    }

    public async Task<(Stream stream, string fileName)?> GetImageAsync(Guid projectId, int imageId, long? userId)
    {
        var data = await context.ProjectImages
            .Where(pi => pi.ProjectId == projectId && pi.Id == imageId)
            .Select(pi => new
            {
                pi.File.Path,
                ProjectViewStatus = pi.Project.ViewStatus,
                ProjectCreatedById = pi.Project.CreatedById,
            })
            .AsNoTracking()
            .SingleOrDefaultAsync();

        if (data == null) return null;

        if (data.ProjectViewStatus == Project.ProjectViewStatus.Private &&
            (!userId.HasValue || userId.Value != data.ProjectCreatedById))
            return null;

        var blobName = Path.GetFileName(data.Path!);
        return await blobStorageService.DownloadAsync(BlobContainers.ProjectImages, blobName);
    }

    /// <summary>
    /// Rejects a write whose RESOLVED interval is inverted, not merely one carrying two
    /// conflicting overrides — a finish-only override landing before the derived start is just
    /// as wrong, and is detectable here because the prints are loaded.
    /// </summary>
    /// <remarks>
    /// The invariant is deliberately NOT maintained afterward: editing an unrelated print can
    /// invert a stored interval later, and failing that print edit to protect a project's
    /// display date would be the worse outcome.
    /// </remarks>
    private static void ValidateProjectDates(
        DateOnly? startOverride,
        DateOnly? finishOverride,
        DateTime createdDate,
        IEnumerable<Print>? prints)
    {
        var (start, finish) = ProjectDateResolver.Resolve(
            startOverride,
            finishOverride,
            createdDate,
            (prints ?? []).Select(p => new ProjectDateResolver.PrintDates(
                p.StartDate, p.PrintTimeInSeconds, p.EstimatedPrintTimeInSeconds)));

        if (finish.HasValue && finish.Value < start)
            throw new BadRequestException("A project's finish date cannot be before its start date.");
    }
}
