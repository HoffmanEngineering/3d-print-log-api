using System;
using System.Threading.Tasks;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;

namespace PrintLogApi.Services
{
    public interface IProjectService
    {
        Task<PagedList<ProjectSummaryDto>> GetProjectSummariesAsync(int pageNumber, int pageSize, long userId);
        Task<Project> GetProjectByIdAsync(Guid id);
        Task<Project> CreateProjectAsync(AddProjectDto dto, long userId);
        Task<Project> UpdateProjectAsync(Guid id, PutProjectDto dto, long userId);
        Task DeleteProjectAsync(Guid id, bool deletePrints, long userId);
    }
}
