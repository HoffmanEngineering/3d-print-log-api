using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;

namespace PrintLogApi.Services
{
    public class ProjectService : IProjectService
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly IBlobStorageService _blobStorageService;
        private readonly ICacheVersionService _cacheVersionService;

        public ProjectService(PrintLogContext context, IMapper mapper, IBlobStorageService blobStorageService, ICacheVersionService cacheVersionService)
        {
            _context = context;
            _mapper = mapper;
            _blobStorageService = blobStorageService;
            _cacheVersionService = cacheVersionService;
        }

        public async Task<Mcp.McpPage<Mcp.ProjectListItem>> ListProjectsForMcp(
            long userId, int page, int pageSize, string search, Project.ProjectStatus? status, System.Threading.CancellationToken ct)
        {
            var query = _context.Projects.AsNoTracking().Where(p => p.CreatedById == userId);
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
            var items = await query
                .OrderByDescending(p => p.UpdatedDate)
                .ThenBy(p => p.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new Mcp.ProjectListItem(
                    p.Id, p.Name, p.Reference, p.Status.ToString(), p.ViewStatus.ToString()))
                .ToListAsync(ct);

            var totalPages = pageSize > 0 ? (int)System.Math.Ceiling(total / (double)pageSize) : 0;
            return new Mcp.McpPage<Mcp.ProjectListItem>(items, page, pageSize, total, totalPages);
        }

        public async Task<Mcp.CreateProjectResult> CreateProjectForMcp(
            long userId, string name, string reference, string description, string url,
            Project.ProjectStatus status, Project.ProjectViewStatus viewStatus, string idempotencyKey,
            System.Threading.CancellationToken ct)
        {
            const string toolName = "create_project";

            idempotencyKey = RequireIdempotencyKey(idempotencyKey);
            string fingerprint = null;
            if (idempotencyKey != null)
            {
                fingerprint = Mcp.McpRequestFingerprint.ComputeCreateProject(
                    name, reference, description, url, status, viewStatus);
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
                CreatedById = userId,
                UpdatedById = userId,
            };

            if (idempotencyKey == null)
            {
                _context.Projects.Add(project);
                await _context.SaveChangesAsync(ct);
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
                    _context.ChangeTracker.Clear();
                    var concurrent = await FindIdempotentProject(userId, toolName, idempotencyKey, fingerprint, ct);
                    if (concurrent != null)
                    {
                        return concurrent;
                    }
                    throw;
                }
            }

            _cacheVersionService.InvalidateUserCache(userId);
            return new Mcp.CreateProjectResult(Describe(project), WasReplayed: false);
        }

        /// <summary>
        /// Creates the project and its idempotency record atomically. Lets DbUpdateException escape:
        /// only the caller can tell a lost unique-index race (replayable) from a genuine write
        /// failure (not), because only it knows the key and fingerprint to look the winner up with.
        /// </summary>
        private async Task CreateProjectWithIdempotencyRecord(
            Project project, long userId, string key, string fingerprint, System.Threading.CancellationToken ct)
        {
            // SqlServerRetryingExecutionStrategy forbids user-initiated transactions unless they run
            // inside an execution strategy, so the whole tx is the retriable unit.
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                using var tx = await _context.Database.BeginTransactionAsync(ct);
                _context.Projects.Add(project);
                await _context.SaveChangesAsync(ct);

                _context.McpIdempotencyRecords.Add(
                    Mcp.McpIdempotencyRecordFactory.ForProject(userId, key, fingerprint, project.Id));
                await _context.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }

        private async Task<Mcp.CreateProjectResult> FindIdempotentProject(
            long userId, string toolName, string key, string fingerprint, System.Threading.CancellationToken ct)
        {
            var record = await _context.McpIdempotencyRecords.AsNoTracking()
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
                ? await _context.Projects.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == projectId.Value && p.CreatedById == userId, ct)
                : null;
            if (project == null)
            {
                throw Mcp.McpToolException.NotFound("The prior result for this idempotency key no longer exists.");
            }

            return new Mcp.CreateProjectResult(Describe(project), WasReplayed: true);
        }

        private static string RequireIdempotencyKey(string key)
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

        private static Mcp.ProjectWriteResult Describe(Project p) => new(
            p.Id, p.Name, p.Reference, p.Description, p.Url, p.Status.ToString(), p.ViewStatus.ToString());

        public async Task<Mcp.ProjectWriteResult> UpdateProjectForMcp(
            long userId, Guid id, string name, string reference, string description, string url,
            Project.ProjectStatus? status, Project.ProjectViewStatus? viewStatus, System.Threading.CancellationToken ct)
        {
            var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id && p.CreatedById == userId, ct);
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
            project.UpdatedById = userId;
            await _context.SaveChangesAsync(ct);
            _cacheVersionService.InvalidateUserCache(userId);
            return Describe(project);
        }

        public async Task<PagedList<ProjectSummaryDto>> GetProjectSummariesAsync(
            int pageNumber, int pageSize, long userId,
            string? search = null, Project.ProjectStatus? status = null, string sortBy = "updatedDate")
        {
            IQueryable<Project> query = _context.Projects
                .Where(p => p.CreatedById == userId)
                .Include(p => p.Images)
                .Include(p => p.Prints)
                    .ThenInclude(pr => pr.FilamentUsage)
                .AsSplitQuery()
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var trimmed = search.Trim();
                query = query.Where(p => p.Name.Contains(trimmed) || p.Reference.Contains(trimmed));
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

            var dtos = items.Select(p => _mapper.Map<ProjectSummaryDto>(p)).ToList();
            return new PagedList<ProjectSummaryDto>(dtos, total, pageNumber, pageSize);
        }

        public async Task<Project> GetProjectByIdAsync(Guid id)
        {
            return await _context.Projects
                .Include(p => p.Images)
                    .ThenInclude(i => i.File)
                .Include(p => p.Prints)
                    .ThenInclude(pr => pr.FilamentUsage)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);
        }

        public async Task<Project> CreateProjectAsync(AddProjectDto dto, long userId)
        {
            var project = _mapper.Map<Project>(dto);
            project.Id = Guid.NewGuid();
            project.CreatedById = userId;
            project.UpdatedById = userId;

            _context.Projects.Add(project);
            await _context.SaveChangesAsync();

            return await GetProjectByIdAsync(project.Id);
        }

        public async Task<Project> UpdateProjectAsync(Guid id, PutProjectDto dto, long userId)
        {
            var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == id);
            if (project == null)
                throw new DoesNotExistException();

            _mapper.Map(dto, project);
            project.UpdatedById = userId;

            await _context.SaveChangesAsync();
            return await GetProjectByIdAsync(id);
        }

        public async Task DeleteProjectAsync(Guid id, bool deletePrints, long userId)
        {
            var project = await _context.Projects
                .Include(p => p.Prints)
                .Include(p => p.Images)
                    .ThenInclude(img => img.File)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (project == null)
                throw new DoesNotExistException();

            if (deletePrints)
            {
                _context.Prints.RemoveRange(project.Prints);
            }
            else
            {
                foreach (var print in project.Prints)
                {
                    print.ProjectId = null;
                }
            }

            foreach (var image in project.Images)
            {
                if (image.File != null)
                    await _blobStorageService.DeleteBlobAsync("projectimages", Path.GetFileName(image.File.Path));
            }

            _context.ProjectImages.RemoveRange(project.Images);
            _context.Projects.Remove(project);
            await _context.SaveChangesAsync();
        }

        public async Task<ProjectImage> AddImageAsync(Guid projectId, IFormFile file, long userId)
        {
            var project = await _context.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
            if (project == null) throw new DoesNotExistException();

            var blobName = $"{Guid.NewGuid()}{System.IO.Path.GetExtension(file.FileName)}";
            using var stream = file.OpenReadStream();
            await _blobStorageService.UploadAsync("projectimages", blobName, stream);

            var fileEntity = new Models.File { Path = blobName, Size = file.Length, CreatedById = userId, UpdatedById = userId };
            _context.Files.Add(fileEntity);
            await _context.SaveChangesAsync();

            var existingCount = await _context.ProjectImages.CountAsync(pi => pi.ProjectId == projectId);
            var image = new ProjectImage
            {
                ProjectId = projectId,
                FileId = fileEntity.Id,
                IsDefault = existingCount == 0,
                DisplayOrder = existingCount,
                CreatedById = userId,
                UpdatedById = userId
            };
            _context.ProjectImages.Add(image);
            await _context.SaveChangesAsync();
            return image;
        }

        public async Task DeleteImageAsync(Guid projectId, int imageId, long userId)
        {
            var image = await _context.ProjectImages
                .Include(pi => pi.File)
                .FirstOrDefaultAsync(pi => pi.ProjectId == projectId && pi.Id == imageId);
            if (image == null) throw new DoesNotExistException();

            if (image.File != null)
                await _blobStorageService.DeleteBlobAsync("projectimages", Path.GetFileName(image.File.Path));

            _context.ProjectImages.Remove(image);
            await _context.SaveChangesAsync();
        }

        public async Task ReorderImagesAsync(Guid projectId, IList<int> orderedImageIds, long userId)
        {
            var images = await _context.ProjectImages
                .Where(pi => pi.ProjectId == projectId)
                .ToListAsync();

            for (int i = 0; i < orderedImageIds.Count; i++)
            {
                var img = images.FirstOrDefault(im => im.Id == orderedImageIds[i]);
                if (img != null) img.DisplayOrder = i;
            }
            await _context.SaveChangesAsync();
        }

        public async Task SetDefaultImageAsync(Guid projectId, int imageId, long userId)
        {
            var images = await _context.ProjectImages
                .Where(pi => pi.ProjectId == projectId)
                .ToListAsync();

            if (!images.Any(img => img.Id == imageId))
                throw new DoesNotExistException();

            foreach (var img in images)
                img.IsDefault = img.Id == imageId;

            await _context.SaveChangesAsync();
        }

        public async Task<(Stream stream, string fileName)?> GetImageAsync(Guid projectId, int imageId, long? userId)
        {
            var data = await _context.ProjectImages
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

            var blobName = Path.GetFileName(data.Path);
            return await _blobStorageService.DownloadAsync("projectimages", blobName);
        }
    }
}
