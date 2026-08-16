using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.Http;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;

namespace PrintLogApi.Services
{
    public interface IProjectService
    {
        /// <summary>Paginated list of the caller's projects for the MCP read surface (name/reference search).</summary>
        Task<McpPage<ProjectListItem>> ListProjectsForMcp(
            long userId, int page, int pageSize, string? search, Project.ProjectStatus? status, CancellationToken ct);

        /// <summary>
        /// Creates a project for the MCP write surface. Invalidates the user cache.
        /// <para>
        /// <paramref name="idempotencyKey"/> is OPTIONAL, matching create_material/create_printer:
        /// with a key, a retry carrying the same arguments replays the original project and a key
        /// reused with different arguments is a conflict; without one, every call creates a new
        /// project.
        /// </para>
        /// </summary>
        Task<CreateProjectResult> CreateProjectForMcp(
            long userId, string name, string? reference, string? description, string? url,
            Project.ProjectStatus status, Project.ProjectViewStatus viewStatus, string? idempotencyKey,
            CancellationToken ct);

        /// <summary>
        /// Creator-only edit of a project for the MCP write surface. Only supplied fields change;
        /// a missing/foreign project surfaces NotFound. Invalidates the user cache.
        /// </summary>
        Task<ProjectWriteResult> UpdateProjectForMcp(
            long userId, Guid id, string? name, string? reference, string? description, string? url,
            Project.ProjectStatus? status, Project.ProjectViewStatus? viewStatus, CancellationToken ct);

        Task<PagedList<ProjectSummaryDto>> GetProjectSummariesAsync(int pageNumber, int pageSize, long userId, string? search = null, Project.ProjectStatus? status = null, string sortBy = "updatedDate");
        Task<Project?> GetProjectByIdAsync(Guid id);
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
