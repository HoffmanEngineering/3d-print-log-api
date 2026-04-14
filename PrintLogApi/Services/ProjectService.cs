using System;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
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

        public ProjectService(PrintLogContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
        }

        public async Task<PagedList<ProjectSummaryDto>> GetProjectSummariesAsync(int pageNumber, int pageSize, long userId)
        {
            var query = _context.Projects
                .Where(p => p.CreatedById == userId)
                .Include(p => p.Images)
                .Include(p => p.Prints)
                    .ThenInclude(pr => pr.FilamentUsage)
                .OrderByDescending(p => p.CreatedDate)
                .AsNoTracking();

            var total = await query.CountAsync();
            var items = await query
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
    }
}
