using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
        Task<ProjectImage> AddImageAsync(Guid projectId, IFormFile file, long userId);
        Task DeleteImageAsync(Guid projectId, int imageId, long userId);
        Task ReorderImagesAsync(Guid projectId, IList<int> orderedImageIds, long userId);
        Task SetDefaultImageAsync(Guid projectId, int imageId, long userId);
        Task<(Stream stream, string fileName)?> GetImageAsync(Guid projectId, int imageId, long? userId);
    }
}
