using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Enums;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;

namespace PrintLogApi.Controllers;

/// <summary>
/// Manage a user's list of filaments
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class FilamentsController(
    IFilamentService filamentService,
    IMapper mapper,
    IFilamentImageService filamentImageService,
    PrintLogContext context,
    IBlobStorageService blobStorageService) : ControllerBase
{
    /// <summary>
    /// Gets a Paged Result of filament summaries for the current user.
    /// </summary>
    /// <param name="pagingRequest">The paging request information.</param>
    /// <param name="sortRequest">The Column and Direction to sort the results for.</param>
    /// <param name="searchText">Search filament's name/description/brand for text.</param>
    /// <param name="filterByMaterialCategoryNickname">Optional filter by a material category nickname</param>
    /// <param name="includeInactive">Include filament rolls that have been marked as inactive.</param>
    /// <param name="showFavoritesOnly">Show only the favoriate filaments</param>
    /// <param name="showLoadedFilamentOnly">Show only currently loaded filament</param>
    /// <returns>A Paged List of filament rolls.</returns>
    /// <response code="200">Returns the paged list of filament rolls.</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedList<FilamentSummaryDto>>> GetFilamentSummariesForUser(
        [FromQuery] PagedRequest pagingRequest,
        [FromQuery] SortRequest<FilamentSummarySortColumn> sortRequest,
        // Nullable because that is what binding already produces: every one of these is
        // optional, and a request that omits the query string binds null here today. Left
        // non-nullable they would pick up MVC's implicit [Required] and 400 instead (#45).
        [FromQuery] string? searchText,
        [FromQuery] string? filterByMaterialCategoryNickname,
        [FromQuery] string? filterByStorageLocation,
        [FromQuery] bool? includeInactive,
        [FromQuery] bool? showFavoritesOnly,
        [FromQuery] bool? showLoadedFilamentOnly,
        [FromQuery] List<ColorPatternType>? colorPatterns = null,
        [FromQuery] List<FilamentFinishType>? finishTypes = null,
        [FromQuery] List<FilamentEffect>? effects = null)
    {
        long? currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return Unauthorized("Please login before requesting filaments.");
        }

        return await filamentService.GetFilamentSummaryForUser(currentUserId.Value,
            sortRequest.SortDirection,
            sortRequest.SortColumn,
            pagingRequest.PageNumber,
            pagingRequest.PageSize,
            searchText,
            filterByMaterialCategoryNickname,
            filterByStorageLocation,
            includeInactive,
            showFavoritesOnly,
            showLoadedFilamentOnly,
            colorPatterns,
            finishTypes,
            effects);
    }


    /// <summary>
    /// Returns detailed information for a filament roll by ID.
    /// </summary>
    /// <param name="id">The GUID of the filament.</param>
    /// <returns></returns>
    /// <response code="200">The Filament details on success.</response>
    /// <response code="403">Returned when the requested filament does not belong to the current user.</response>
    /// <response code="404">Returned when a filament with that GUID does not exist.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FilamentDetailDto>> GetFilament(Guid id)
    {
        var filament = await filamentService.GetFilamentById(id);

        if (filament == null)
        {
            return NotFound();
        }


        var currentUserId = User.GetUserId();

        if (currentUserId != filament.CreatedById)
        {
            return Forbid();
        }

        // Hydration runs here rather than in the service because GetFilamentById returns the
        // entity and the controller owns the map. Signing is never a member mapping.
        var dto = mapper.Map<FilamentDetailDto>(filament);
        await filamentService.HydrateDetailImageUrlsAsync(dto, HttpContext.RequestAborted);
        return dto;
    }

    /// <summary>
    /// Update an existing filament with new information.
    /// </summary>
    /// <param name="id">The GUID of the filament to update.</param>
    /// <param name="filamentDto">The updated filament details.</param>
    /// <returns>The updated filament.</returns>
    /// <response code="201">The updated filament information.</response>
    /// <response code="400">Returned if the id does not match the id of the filament details provided.</response>
    /// <response code="401">Returned if the request is not authenticated.</response>
    /// <response code="403">Returned if the current user tries to update a filament which is not theirs.</response>
    /// <response code="404">Returned if a filament with the specified ID is not found.</response>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutFilament(Guid id, FilamentDetailDto filamentDto)
    {
        if (id != filamentDto.Id)
        {
            return BadRequest("ID in route does not match body.");
        }

        var existingFilament = await filamentService.GetFilamentById(id);

        if (existingFilament == null)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (!await filamentService.CanUserAccessFilament(userId.Value, id))
        {
            return Forbid();
        }

        try
        {
            var updatedPrint = await filamentService.UpdateFilament(id, filamentDto, userId.Value);

            var updatedDto = mapper.Map<FilamentDetailDto>(updatedPrint);
            await filamentService.HydrateDetailImageUrlsAsync(updatedDto, HttpContext.RequestAborted);

            return CreatedAtAction("GetFilament", new { id = existingFilament.Id }, updatedDto);
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
    }

    /// <summary>
    ///     Create a new Filament for the current user.
    /// </summary>
    /// <param name="filamentDto">The dto containing all of the details for the filament to create.</param>
    /// <returns>The filament detail DTO that was created.</returns>
    /// <response code="201">The filament detail DTO that was created.</response>
    /// <response code="401">Returned if the request is not authenticated.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<FilamentDetailDto>> PostFilament(AddFilamentDto filamentDto)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var newFilament = await filamentService.AddFilament(filamentDto, userId.Value);


        return CreatedAtAction("GetFilament", new { id = newFilament.Id }, mapper.Map<Filament, FilamentDetailDto>(newFilament));
    }

    /// <summary>
    /// Permantently delete a filament, if the filament has not been used in any existing prints.
    /// </summary>
    /// <param name="id">The ID of the filament to delete.</param>
    /// <response code="204">Returned if the filament was deleted successfully.</response>
    /// <response code="400">Returned if the filament is unable to be deleted since it has been used in a print.</response>
    /// <response code="403">Returned if the current user cannot access the filament.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteFilament(Guid id)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }


        if (!await filamentService.CanUserAccessFilament(userId.Value, id))
        {
            return Forbid();
        }

        try
        {
            await filamentService.DeleteFilament(id);
        }
        catch (FilamentIsInUseException)
        {
            return BadRequest("This Filament is used in a Print and cannot be deleted. Try editing the Filament and marking it as Inactive instead.");
        }

        return NoContent();
    }

    /// <summary>
    /// Returns a DTO which includes a list of all filament storage locations for the current user.
    /// </summary>
    /// <returns></returns>
    /// <response code="200">The Filament Storage Locations on success.</response>
    [HttpGet("storage-locations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FilamentStorageLocationDto>> GetFilamentStorageLocations()
    {

        var currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var locations = await filamentService.GetFilamentStorageLocations(currentUserId.Value);

        return new FilamentStorageLocationDto { StorageLocations = locations };
    }

    /// <summary>
    /// Returns a DTO which includes a list of all filament purchase locations for the current user.
    /// </summary>
    /// <returns></returns>
    /// <response code="200">The Filament Purchase Locations on success.</response>
    [HttpGet("purchase-locations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FilamentPurchaseLocationsDto>> GetFilamentPurchaseLocations()
    {

        var currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var locations = await filamentService.GetFilamentPurchaseLocations(currentUserId.Value);

        return new FilamentPurchaseLocationsDto { PurchaseLocations = locations };
    }


    /// <summary>
    /// Returns a DTO which includes a list of all filament brands for the current user.
    /// </summary>
    /// <returns></returns>
    /// <response code="200">The Filament Brands on success.</response>
    [HttpGet("brands")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FilamentBrandsDto>> GetFilamentBrands()
    {

        var currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var brands = await filamentService.GetFilamentBrands(currentUserId.Value);

        return new FilamentBrandsDto { Brands = brands };
    }

    /// <summary>
    /// Upload an image to a filament.
    /// </summary>
    /// <remarks>
    /// The uploaded bytes are decoded server-side; the client's declared content type is
    /// not trusted. An undecodable or disallowed file is rejected before anything is stored.
    /// </remarks>
    /// <response code="201">The stored image, with signed URLs.</response>
    /// <response code="400">The file is missing, too large, or not a supported image.</response>
    /// <response code="404">No such filament belonging to the current user.</response>
    [HttpPost("{id}/images")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FilamentImageDto>> PostFilamentImage(Guid id, IFormFile file)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        if (file is null || file.Length == 0) return BadRequest("Image file is required.");

        const long maxImageSizeBytes = 10 * 1024 * 1024;
        if (file.Length > maxImageSizeBytes) return BadRequest("Image must be under 10MB.");

        try
        {
            await using var stream = file.OpenReadStream();
            var image = await filamentImageService.AddImageAsync(
                id, stream, userId.Value, HttpContext.RequestAborted);

            var dto = await filamentService.HydrateImageDtoAsync(image, HttpContext.RequestAborted);

            return CreatedAtAction(nameof(GetFilamentImage), new { id, imageId = image.Id }, dto);
        }
        catch (InvalidImageException ex) { return BadRequest(ex.Message); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Redirects to a signed URL for a filament image.
    /// </summary>
    /// <remarks>
    /// For non-browser API consumers (MCP server, scripts) holding a bearer token.
    /// The UI does not use this - it reads pre-signed URLs from the filament DTO.
    /// NOT usable from &lt;img src&gt;: the redirect itself requires the bearer token.
    /// </remarks>
    [HttpGet("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFilamentImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        // Ownership is part of the predicate, so a foreign image is indistinguishable
        // from a missing one. The endpoint must not be an existence oracle.
        var image = await context.FilamentImages
            .AsNoTracking()
            .Include(fi => fi.File)
            .FirstOrDefaultAsync(fi => fi.FilamentId == id
                                    && fi.Id == imageId
                                    && fi.Filament.CreatedById == userId.Value,
                                 HttpContext.RequestAborted);
        if (image?.File?.Path is null) return NotFound();

        var uri = await blobStorageService.GenerateSasInlineUrlAsync(
            BlobContainers.FilamentImages, Path.GetFileName(image.File.Path),
            image.ContentType, TimeSpan.FromHours(6), TimeSpan.FromHours(5));

        return Redirect(uri.ToString());
    }

    /// <summary>
    /// Deletes a filament image, its file rows, and its blobs.
    /// </summary>
    [HttpDelete("{id}/images/{imageId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFilamentImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await filamentImageService.DeleteImageAsync(
                id, imageId, userId.Value, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Reorders a filament's images. The supplied IDs must be the filament's exact image set.
    /// </summary>
    [HttpPut("{id}/images/reorder")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderFilamentImages(Guid id, [FromBody] List<int> orderedImageIds)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await filamentImageService.ReorderImagesAsync(
                id, orderedImageIds, userId.Value, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (DoesNotExistException) { return NotFound(); }
    }

    /// <summary>
    /// Makes one of a filament's images its default.
    /// </summary>
    [HttpPost("{id}/images/{imageId}/set-as-default")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultFilamentImage(Guid id, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue) return Unauthorized();

        try
        {
            await filamentImageService.SetDefaultImageAsync(
                id, imageId, userId.Value, HttpContext.RequestAborted);
            return NoContent();
        }
        catch (DoesNotExistException) { return NotFound(); }
    }
}
