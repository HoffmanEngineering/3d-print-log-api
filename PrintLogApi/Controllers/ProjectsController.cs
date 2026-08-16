using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class ProjectsController(
    IProjectService projectService,
    IMapper mapper,
    ICacheVersionService cacheVersionService) : ControllerBase
{
    /// <summary>Get a paged list of the current user's projects.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedList<ProjectSummaryDto>>> GetProjects(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] Project.ProjectStatus? status = null,
        [FromQuery] string sortBy = "updatedDate")
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        var result = await projectService.GetProjectSummariesAsync(pageNumber, pageSize, userId.Value, search, status, sortBy);
        return Ok(result);
    }

    /// <summary>Get a project's full detail by ID. Public/Unlisted projects are accessible without auth.</summary>
    [HttpGet("{id}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDetailDto>> GetProjectById(Guid id)
    {
        var project = await projectService.GetProjectByIdAsync(id);
        if (project == null)
            return NotFound();

        var currentUserId = User.GetUserId();
        var isOwner = currentUserId.HasValue && project.CreatedById == currentUserId.Value;

        if (project.ViewStatus == Project.ProjectViewStatus.Private && !isOwner)
            return Forbid();

        return Ok(mapper.Map<ProjectDetailDto>(project));
    }

    /// <summary>Create a new project.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ProjectDetailDto>> PostProject(AddProjectDto dto)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        var project = await projectService.CreateProjectAsync(dto, userId.Value);
        cacheVersionService.InvalidateUserCache(userId.Value);

        return CreatedAtAction(nameof(GetProjectById), new { id = project.Id },
            mapper.Map<ProjectDetailDto>(project));
    }

    /// <summary>Update a project's metadata.</summary>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDetailDto>> PutProject(Guid id, PutProjectDto dto)
    {
        if (id != dto.Id)
            return BadRequest("ID in route does not match body.");

        var existing = await projectService.GetProjectByIdAsync(id);
        if (existing == null)
            return NotFound();

        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        if (existing.CreatedById != userId.Value)
            return Forbid();

        var updated = await projectService.UpdateProjectAsync(id, dto, userId.Value);
        cacheVersionService.InvalidateUserCache(userId.Value);

        return Ok(mapper.Map<ProjectDetailDto>(updated));
    }

    /// <summary>
    /// Delete a project. If deletePrints=true, also deletes member prints.
    /// If deletePrints=false (default), prints are unlinked and become standalone.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteProject(Guid id, [FromQuery] bool deletePrints = false)
    {
        var existing = await projectService.GetProjectByIdAsync(id);
        if (existing == null)
            return NotFound();

        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        if (existing.CreatedById != userId.Value)
            return Forbid();

        try
        {
            await projectService.DeleteProjectAsync(id, deletePrints, userId.Value);
            cacheVersionService.InvalidateUserCache(userId.Value);
            return Ok();
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
    }

    /// <summary>Get a project image.</summary>
    [AllowAnonymous]
    [HttpGet("{id}/images/{imageId}")]
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Client, NoStore = false)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [MediaEndpoint]
    public async Task<IActionResult> GetProjectImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        var result = await projectService.GetImageAsync(id, imageId, userId);
        if (result == null) return NotFound();

        new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider()
            .TryGetContentType(result.Value.fileName, out var contentType);
        return File(result.Value.stream, contentType ?? "application/octet-stream");
    }

    /// <summary>Upload an image to a project.</summary>
    [HttpPost("{id}/images")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectImageDto>> PostProjectImage(Guid id, IFormFile file)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        var project = await projectService.GetProjectByIdAsync(id);
        if (project == null) return NotFound();
        if (project.CreatedById != userId.Value) return Forbid();

        if (file == null)
            return BadRequest("Image file is required.");

        var allowedImageTypes = new[] { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp" };
        if (!allowedImageTypes.Contains(file.ContentType.ToLowerInvariant()))
            return BadRequest("Only image files are accepted (jpeg, png, gif, webp, bmp).");

        const long maxImageSizeBytes = 10 * 1024 * 1024;
        if (file.Length > maxImageSizeBytes)
            return BadRequest("Image must be under 10MB.");

        try
        {
            var image = await projectService.AddImageAsync(id, file, userId.Value);
            cacheVersionService.InvalidateUserCache(userId.Value);
            return CreatedAtAction(nameof(GetProjectImage), new { id, imageId = image.Id }, mapper.Map<ProjectImageDto>(image));
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
    }

    /// <summary>Remove an image from a project.</summary>
    [HttpDelete("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteProjectImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        var project = await projectService.GetProjectByIdAsync(id);
        if (project == null) return NotFound();
        if (project.CreatedById != userId.Value) return Forbid();

        try
        {
            await projectService.DeleteImageAsync(id, imageId, userId.Value);
            cacheVersionService.InvalidateUserCache(userId.Value);
            return Ok();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>Reorder a project's images.</summary>
    [HttpPut("{id}/images/reorder")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ReorderProjectImages(Guid id, [FromBody] IList<int> orderedImageIds)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        var project = await projectService.GetProjectByIdAsync(id);
        if (project == null) return NotFound();
        if (project.CreatedById != userId.Value) return Forbid();

        await projectService.ReorderImagesAsync(id, orderedImageIds, userId.Value);
        cacheVersionService.InvalidateUserCache(userId.Value);
        return Ok();
    }

    /// <summary>Set a project image as the default.</summary>
    [HttpPost("{id}/images/{imageId}/set-as-default")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetProjectImageAsDefault(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        var project = await projectService.GetProjectByIdAsync(id);
        if (project == null) return NotFound();
        if (project.CreatedById != userId.Value) return Forbid();

        try
        {
            await projectService.SetDefaultImageAsync(id, imageId, userId.Value);
            cacheVersionService.InvalidateUserCache(userId.Value);
            return Ok();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }
}
