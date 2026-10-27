using AutoMapper;
using Microsoft.ApplicationInsights;
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
    ICacheVersionService cacheVersionService,
    TelemetryClient telemetry,
    IBlobStorageService blobStorage,
    SocialImageLookup socialImages) : ControllerBase
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

    /// <summary>
    /// Returns the ids of every Public project, for the UI's deploy-time sitemap.
    /// </summary>
    /// <remarks>
    /// Each id is one <c>GET {id}</c> serves to an anonymous caller. Unlisted projects are left out
    /// even though they are viewable by link, matching <c>/api/Prints/public</c>.
    /// </remarks>
    /// <response code="200">The public project ids, ordered.</response>
    // Declared before {id} only for reading order: the literal segment outranks the parameter regardless.
    [AllowAnonymous]
    [HttpGet("public")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<ActionResult<IEnumerable<Guid>>> GetPublicProjectIds(CancellationToken cancellationToken)
    {
        telemetry.TrackEvent("PublicProjectsQueried");
        return await projectService.GetPublicProjectIdsAsync(cancellationToken);
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

        if (!isOwner && !Project.IsViewableByNonOwner(project.ViewStatus))
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

        try
        {
            var project = await projectService.CreateProjectAsync(dto, userId.Value);
            cacheVersionService.InvalidateUserCache(userId.Value);
            telemetry.TrackEvent("ProjectAdded");

            return CreatedAtAction(nameof(GetProjectById), new { id = project.Id },
                mapper.Map<ProjectDetailDto>(project));
        }
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
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

        try
        {
            var updated = await projectService.UpdateProjectAsync(id, dto, userId.Value);
            cacheVersionService.InvalidateUserCache(userId.Value);
            telemetry.TrackEvent("ProjectEdit");

            return Ok(mapper.Map<ProjectDetailDto>(updated));
        }
        // There is no global exception-to-status mapping in this app, so without the catch an
        // inverted date range surfaces as a 500.
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
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
            telemetry.TrackEvent("ProjectDelete", new Dictionary<string, string>
            {
                ["deletePrints"] = deletePrints ? "true" : "false",
            });
            return Ok();
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
    }

    /// <summary>Get a project image.</summary>
    /// <remarks>
    /// Anyone can read an image of a public or unlisted project; a private project's images only
    /// its owner (anyone else gets 404). Responses carry a strong `ETag` (answering
    /// `If-None-Match` with 304) and support `Range`. An image anyone can read is
    /// `Cache-Control: public, max-age=3600`; a private project's image is `private`.
    /// </remarks>
    /// <response code="200">The image.</response>
    /// <response code="206">The requested byte range of the image.</response>
    /// <response code="304">The image has not changed since the `ETag` sent in `If-None-Match`.</response>
    /// <response code="404">No such image, or the project is private and the caller is not its owner.</response>
    [AllowAnonymous]
    [HttpGet("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [MediaEndpoint]
    public Task<IActionResult> GetProjectImage(Guid id, int imageId)
        => ServeProjectImageAsync(id, imageId, thumbnail: false);

    /// <summary>Get the 320px thumbnail of a project image, for list views.</summary>
    /// <remarks>
    /// Same access, caching and headers as the image itself. An image stored before thumbnails
    /// existed returns the original instead.
    /// </remarks>
    /// <response code="200">The thumbnail, or the original when the image has none.</response>
    /// <response code="206">The requested byte range.</response>
    /// <response code="304">Unchanged since the `ETag` sent in `If-None-Match`.</response>
    /// <response code="404">No such image, or the project is private and the caller is not its owner.</response>
    [AllowAnonymous]
    [HttpGet("{id}/images/{imageId}/thumbnail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [MediaEndpoint]
    public Task<IActionResult> GetProjectImageThumbnail(Guid id, int imageId)
        => ServeProjectImageAsync(id, imageId, thumbnail: true);

    /// <summary>Get the link-preview image of a public project: its default image, about 1200px on the long edge.</summary>
    /// <remarks>
    /// This is the `og:image` the project's `/seo` metadata points at, with a `?v=` that changes
    /// when the image does; the parameter is otherwise ignored. Returns the original when the
    /// image has no social derivative yet. 404 for a project that is not public (even to its
    /// owner) or has no image.
    /// </remarks>
    /// <response code="200">The image.</response>
    /// <response code="206">The requested byte range.</response>
    /// <response code="304">Unchanged since the `ETag` sent in `If-None-Match`.</response>
    /// <response code="404">The project does not exist, is not public, or has no image.</response>
    [AllowAnonymous]
    [HttpGet("{id}/social-image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [MediaEndpoint]
    public async Task<IActionResult> GetProjectSocialImage(Guid id)
    {
        var image = await socialImages.ForProjectAsync(id, HttpContext.RequestAborted);
        if (image is null) return NotFound();

        return await ImageFileResult.ServeAsync(
            this, blobStorage, BlobContainers.ProjectImages, image.Served, HttpContext.RequestAborted);
    }

    private async Task<IActionResult> ServeProjectImageAsync(Guid id, int imageId, bool thumbnail)
    {
        var image = await projectService.GetImageRefAsync(
            id, imageId, User.GetUserId(), thumbnail, HttpContext.RequestAborted);
        if (image == null) return NotFound();

        return await ImageFileResult.ServeAsync(
            this, blobStorage, BlobContainers.ProjectImages, image, HttpContext.RequestAborted);
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

        // A cheap early answer for an obviously wrong file. The decode in the service decides.
        var allowedImageTypes = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowedImageTypes.Contains(file.ContentType.ToLowerInvariant()))
            return BadRequest("Only image files are accepted (jpeg, png, webp).");

        const long maxImageSizeBytes = 10 * 1024 * 1024;
        if (file.Length > maxImageSizeBytes)
            return BadRequest("Image must be under 10MB.");

        try
        {
            var image = await projectService.AddImageAsync(id, file, userId.Value);
            cacheVersionService.InvalidateUserCache(userId.Value);
            telemetry.TrackEvent("ProjectPictureAdded");
            return CreatedAtAction(nameof(GetProjectImage), new { id, imageId = image.Id }, mapper.Map<ProjectImageDto>(image));
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
        catch (InvalidImageException ex)
        {
            return BadRequest(ex.Message);
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
