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

        public async Task<Mcp.ProjectWriteResult> CreateProjectForMcp(
            long userId, string name, string reference, string description, string url,
            Project.ProjectStatus status, Project.ProjectViewStatus viewStatus, System.Threading.CancellationToken ct)
        {
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
            _context.Projects.Add(project);
            await _context.SaveChangesAsync(ct);
            _cacheVersionService.InvalidateUserCache(userId);
            return new Mcp.ProjectWriteResult(project.Id, project.Name, project.Status.ToString(), project.ViewStatus.ToString());
        }

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
            return new Mcp.ProjectWriteResult(project.Id, project.Name, project.Status.ToString(), project.ViewStatus.ToString());
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
