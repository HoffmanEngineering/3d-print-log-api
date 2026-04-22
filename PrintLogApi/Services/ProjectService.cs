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

        public ProjectService(PrintLogContext context, IMapper mapper, IBlobStorageService blobStorageService)
        {
            _context = context;
            _mapper = mapper;
            _blobStorageService = blobStorageService;
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
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(p => p.Name.Contains(search.Trim()));

            if (status.HasValue)
                query = query.Where(p => p.Status == status.Value);

            var orderedQuery = sortBy == "createdDate"
                ? query.OrderByDescending(p => p.CreatedDate)
                : query.OrderByDescending(p => p.UpdatedDate);

            var total = await orderedQuery.CountAsync();
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
                .FirstOrDefaultAsync(pi => pi.ProjectId == projectId && pi.Id == imageId);
            if (image == null) throw new DoesNotExistException();
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
