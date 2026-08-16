using System.ComponentModel.DataAnnotations;
using System.Globalization;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers;

/// <summary>
/// Operations involving Prints, print filament, print images, and print comments.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PrintsController(
    PrintLogContext context,
    IMapper mapper,
    IAuthorizationService authorizationService,
    TelemetryClient telemetry,
    IPrintService printService,
    IPrintImageService printImageService,
    ICommentService commentService,
    IMemoryCache cache,
    ICacheVersionService cacheVersionService,
    IBlobStorageService blobStorageService,
    IFileAttachmentService fileAttachmentService) : ControllerBase
{
    private readonly string printImageContainerName = "printimages";
    private const string PRINT_SUMMARY_CACHE_PREFIX = "print_summary_";

    /// <summary>
    ///     Get a paged list of Print Summary information for a user. 
    ///     If no userId is provided, then all prints for the currently authenticated user will be queried.
    ///     Otherwise, if a userId is provided, then only the user's public prints will be returned.
    /// </summary>
    /// <param name="pagingRequest">The paging request.</param>
    /// <param name="searchText">Optionally search for text in a print's title or notes.</param>
    /// <param name="filterByPrinterIds">Optionally filter by specific printer ids.</param>
    /// <param name="sortRequest">The sorting request.</param>
    /// <param name="filterByFilamentIds">Optionally filter by specific filament ids.</param>
    /// <param name="filterByStatus">Optionally filter by a specific print status. <see cref="PrintStatus"/></param>
    /// <param name="userId">Optionally search for public</param>
    /// <param name="filterByProjectId">Optionally filter prints belonging to a specific project.</param>
    /// <returns>A Paged List of Print Summaries matching the search criteria.</returns>
    /// <response code="200">A Paged List of Print Summaries matching the search criteria.</response>
    /// <response code="400">Returned if no user is logged in, and no userId is provided.</response>
    [HttpGet("summary")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedList<PrintSummaryDTO>>> GetPrintSummary(
        [FromQuery] PagedRequest pagingRequest,
        // Optional filters: absent from the query string means null here today, and must keep
        // meaning that rather than becoming an implicit [Required] 400 (#45). The nullable
        // siblings further down already say the same thing with a `= null` default.
        [FromQuery, MaxLength(50)] string? searchText,
        // The collection filters stay non-nullable: the binder supplies an empty sequence when
        // the query string omits them, and an implicit [Required] is satisfied by that.
        [FromQuery] IEnumerable<long> filterByPrinterIds,
        [FromQuery] SortRequest<PrintSummarySortColumn> sortRequest,
        [FromQuery] IEnumerable<Guid> filterByFilamentIds,
        [FromQuery] Print.PrintStatus? filterByStatus,
        [FromQuery] long? userId,
        [FromQuery] Guid? filterByProjectId = null,
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] DateTimeOffset? toDate = null,
        [FromQuery] IEnumerable<Print.PrintStatus>? filterByStatuses = null,
        [FromQuery] IEnumerable<Guid>? filterByProjectIds = null)
    {

        long? currentUserId = User.GetUserId();

        // Same condition as `!userId.HasValue && !currentUserId.HasValue`, written so the
        // resulting id is a non-nullable local the compiler can follow thirty lines down to
        // its use. currentUserId itself is still needed separately for the cache key.
        if ((userId ?? currentUserId) is not { } targetUserId)
        {
            return BadRequest("User is not logged in, and summary is not filtered by a specific userId. Please log in and try again.");
        }

        // A one-sided range must be rejected, not ignored: silently returning unbounded
        // results for ?fromDate=... alone looks like a filter that ran and found everything.
        // Matches AnalyticsFilter.Validate, so both endpoints answer the same way.
        if (fromDate.HasValue != toDate.HasValue)
        {
            return BadRequest("fromDate and toDate must be supplied together.");
        }

        if (fromDate.HasValue && toDate.HasValue && fromDate >= toDate)
        {
            return BadRequest("fromDate must be earlier than toDate.");
        }

        // Fold the legacy scalar parameters into the collections so downstream has one code
        // path and so ?filterByStatus=Success and ?filterByStatuses=Success share a cache entry.
        var statuses = (filterByStatuses ?? Enumerable.Empty<Print.PrintStatus>()).ToList();
        if (filterByStatus.HasValue && !statuses.Contains(filterByStatus.Value))
        {
            statuses.Add(filterByStatus.Value);
        }

        var projectIds = (filterByProjectIds ?? Enumerable.Empty<Guid>()).ToList();
        if (filterByProjectId.HasValue && !projectIds.Contains(filterByProjectId.Value))
        {
            projectIds.Add(filterByProjectId.Value);
        }

        var version = cacheVersionService.GetUserCacheVersion(targetUserId);
        var cacheKey = GenerateCacheKey(targetUserId, currentUserId, version, pagingRequest, searchText,
                                        filterByPrinterIds, filterByFilamentIds, sortRequest, statuses, projectIds,
                                        fromDate, toDate);

        // Null-forgiven: only a non-null result is ever stored under this key.
        if (cache.TryGetValue(cacheKey, out PagedList<PrintSummaryDTO>? cachedResult))
        {
            return cachedResult!;
        }

        var result = await printService.SearchPrintSummary(pagingRequest, searchText, sortRequest, filterByPrinterIds, filterByFilamentIds, statuses, userId, currentUserId, projectIds, fromDate, toDate);

        var cacheOptions = new MemoryCacheEntryOptions()
            .SetSize(EstimateCacheSize(result))
            .SetSlidingExpiration(TimeSpan.FromMinutes(5))
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(15))
            .SetPriority(CacheItemPriority.Normal);

        cache.Set(cacheKey, result, cacheOptions);

        return result;
    }


    /// <summary>
    /// Get Print Statistics for the current user.
    /// </summary>
    /// <returns></returns>
    [HttpGet("stats")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Client, NoStore = false)]
    public async Task<ActionResult<List<PrintStatistic>>> GetPrintStats([FromQuery] DateTimeOffset fromDate, [FromQuery] DateTimeOffset toDate)
    {

        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var printStats = await printService.GetPrintStatisticsForUser(userId.Value, fromDate, toDate);

        return printStats;
    }



    /// <summary>
    /// Returns a chronologically interleaved list of project rows and standalone print rows for the current user,
    /// with optional filtering and sorting.
    /// </summary>
    [HttpGet("grouped")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedList<GroupedFeedItemDto>>> GetGrouped(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery, MaxLength(50)] string? searchText = null,
        [FromQuery] IEnumerable<long>? filterByPrinterIds = null,
        [FromQuery] IEnumerable<Guid>? filterByFilamentIds = null,
        [FromQuery] Print.PrintStatus? filterByStatus = null,
        [FromQuery] SortRequest<PrintSummarySortColumn>? sortRequest = null)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
            return Unauthorized();

        var result = await printService.GetGroupedFeedAsync(
            pageNumber, pageSize, userId.Value,
            searchText, filterByPrinterIds, filterByFilamentIds,
            filterByStatus, sortRequest);

        return Ok(result);
    }

    /// <summary>
    ///     Get a print's detailed information by print id.
    /// </summary>
    /// <param name="id">The id of a print to query</param>
    /// <returns></returns>
    /// <response code="200">Returns the Print's Detailed information.</response>
    /// <response code="403">
    ///     Returned when the current user (authenticated or not) cannot access the requested print id.
    ///     Normally when the print is marked as private, and the current user cannot access it.
    /// </response>
    /// <response code="404">Returned when a print with that ID does not exist.</response>
    [HttpGet("{id}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintDetailDTO>> GetPrintById(long id)
    {
        var print = await printService.GetPrintById(id);

        if (print == null)
        {
            return NotFound();
        }

        if (!await CanViewPrint(print))
        {
            return Forbid();
        }

        var printDetailDto = await context.Prints
            .Include(p => p.Printer)
            .Include(p => p.Images!)
                .ThenInclude(p => p.File)
            .Include(p => p.Comments!)
                .ThenInclude(p => p.Comment)
            .Include(p => p.FilamentUsage!)
                .ThenInclude(pf => pf.Filament)
            .Where(p => p.Id == id)
            .AsNoTracking()
            .ProjectTo<PrintDetailDTO>(mapper.ConfigurationProvider)
            .FirstOrDefaultAsync();

        // Not provably non-null: the existence check above and this projection are separate
        // queries, so a delete in between yields null here. The null-forgive preserves the
        // pre-existing NullReferenceException rather than papering over the race; closing it
        // properly is a behaviour change, tracked in #57.
        printDetailDto!.Comments = printDetailDto.Comments!.OrderBy(c => c.CreatedDate).ToList();

        return printDetailDto;
    }

    /// <summary>
    ///     Generate a print report for the current user.
    /// </summary>
    /// <returns>Returns a octet-stream containing a comma-separated value (.csv) file with a report of the current user's print information.</returns>
    /// <response code="200">Returns a octet-stream containing a comma-separated value (.csv) file with a report of the current user's print information.</response>
    /// <response code="401">Returned when no user is currently authenticated.</response>
    [HttpGet("csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllPrintDetailsAsCsv(CancellationToken cancellationToken)
    {
        long? currentUserId = User.GetUserId();

        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        // Written straight to the response body instead of returning a File() result: the service
        // streams rows as it reads them, so there is no Stream to hand back. The headers therefore
        // have to be set before the first byte goes out. Content type stays application/octet-stream
        // — text/csv is arguably more correct but is a caller-visible change (see #65).
        var contentDisposition = new ContentDispositionHeaderValue("attachment");
        contentDisposition.SetHttpFileName("PrintReports.csv");

        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = contentDisposition.ToString();

        await printService.WritePrintReportAsCsvForUser(currentUserId.Value, Response.Body, cancellationToken);

        return new EmptyResult();
    }


    /// <summary>
    ///     Update a print with new detailed information. All data is overridden with the details provided, no partial-patching is done. Last-request wins.
    ///     Normally GetPrintById is used to retrieve the current version, then fields are modified before PUT to this endpoint.
    /// </summary>
    /// <param name="id">The ID of the print to update.</param>
    /// <param name="printDTO">The new print detail information.</param>
    /// <response code="200">The newly-updated Print Detail information.</response>
    /// <response code="401">Returned when no user is authenticated.</response>
    /// <response code="403">Returned when the current authenticated user does not have access to update the requested print.</response>
    /// <response code="404">Returned when the printDTO is not valid, or if the printDTO's ID does not match the ID in the route.</response>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintDetailDTO>> PutPrint(long id, PutPrintDetailDto printDTO)
    {
        if (id != printDTO.Id)
        {
            return BadRequest("ID in route does not match body.");
        }

        var existingPrint = await printService.GetPrintById(id);

        if (existingPrint == null)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        if (userId != existingPrint.CreatedById && userId != existingPrint.Printer.UserId)
        {
            return Forbid();
        }

        try
        {
            var updatedPrint = await printService.UpdatePrint(id, printDTO, userId.Value);

            cacheVersionService.InvalidateUserCache(userId.Value);

            return CreatedAtAction("GetPrintById", new { id = existingPrint.Id }, mapper.Map<PrintDetailDTO>(updatedPrint));
        }
        catch (UserCannotAccessPrinterException)
        {
            return BadRequest();
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }



    }

    /// <summary>
    ///   Update a print with a new PrintStatus.
    /// </summary>
    /// <param name="id">The ID of the print to update.</param>
    /// <param name="newStatus">The new Print Status.</param>
    /// <response code="200">The updated Print Detail information.</response>
    /// <response code="403">Returned if the currently authenticated user does not have access to update the requested print.</response>
    /// <response code="404">Returned if no print is found with the requested id.</response>
    [HttpPut("{id}/status/{newStatus}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintDetailDTO>> UpdatePrintStatus(long id, PrintStatus newStatus)
    {

        var existingPrint = await printService.GetPrintById(id);

        if (existingPrint == null)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }
        if (userId != existingPrint.CreatedById && userId != existingPrint.Printer.UserId)
        {
            return Forbid();
        }

        try
        {
            var updatedPrint = await printService.UpdatePrintStatus(id, newStatus, userId.Value);

            cacheVersionService.InvalidateUserCache(userId.Value);

            return CreatedAtAction("GetPrintById", new { id = existingPrint.Id }, mapper.Map<PrintDetailDTO>(existingPrint));
        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
        catch (UserCannotAccessFilamentException)
        {
            return BadRequest("Selected filament does not belong to currently logged in user.");
        }

    }

    /// <summary>
    ///    Create a new Print.
    /// </summary>
    /// <param name="print">The print details to create.</param>
    /// <response code="201">Returned if the create was successful, containing the new Print Detail information.</response>
    /// <response code="400">Returned if the new Print is not valid. Inspect Problem Details object for message as to what failed validation.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PrintDetailDTO>> PostPrint(AddPrintDTO print)
    {
        var userId = User.GetUserId();

        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var newPrint = await printService.AddPrint(print, userId.Value);
            telemetry.TrackEvent("PrintAdded");

            cacheVersionService.InvalidateUserCache(userId.Value);

            return CreatedAtAction("GetPrintById", new { id = newPrint.Id }, mapper.Map<PrintDetailDTO>(newPrint));
        }
        catch (UserCannotAccessPrinterException)
        {
            return BadRequest("Selected printer does not belong to currently logged in user.");
        }
        catch (UserCannotAccessFilamentException)
        {
            return BadRequest("Selected filament does not belong to currently logged in user.");
        }

    }



    /// <summary>
    ///     Delete a print permanently. Deleted prints will delete any associated print data, such as comments, filament usage, images, etc.
    /// </summary>
    /// <param name="id">The id of the print to delete.</param>
    /// <response code="200">When the print was deleted successfully.</response>
    /// <response code="401">Returned when the user is unauthorized.</response>
    /// <response code="403">Returned when the current user does not have access to delete/modify the requested print.</response>
    /// <response code="404">Returned if there is no print with the requested id.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintDetailDTO>> DeletePrint(long id)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingPrint = await printService.GetPrintById(id);

        if (existingPrint == null)
        {
            return NotFound();
        }


        if (userId != existingPrint.CreatedById)
        {
            return Forbid();
        }

        await printService.DeletePrint(existingPrint);

        cacheVersionService.InvalidateUserCache(userId.Value);

        var properties = new Dictionary<string, string> {
            { "PrintId", existingPrint.Id.ToString() },
            { "UserId", userId.ToString()! },
            { "PrintCreated", existingPrint.CreatedDate.ToString("O", CultureInfo.InvariantCulture) }
        };
        telemetry.TrackEvent("PrintDeleted", properties);

        return Ok();
    }

    /// <summary>
    /// Gets an image attached to a print.
    /// </summary>
    /// <param name="printId">The Id of the print.</param>
    /// <param name="imageId">The id of the image to retrieve.</param>
    /// <returns></returns>
    [AllowAnonymous]
    [HttpGet("{printId}/image/{imageId}")]
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Client, NoStore = false)]
    [MediaEndpoint]
    public async Task<IActionResult> GetImage(long printId, int imageId)
    {
        // Optimized query: only load the specific image and minimal print data needed for authorization
        var imageData = await context.PrintImages
            .Where(pi => pi.PrintId == printId && pi.Id == imageId)
            .Select(pi => new
            {
                pi.File,
                PrintViewStatus = pi.Print.ViewStatus,
                PrintCreatedById = pi.Print.CreatedById
            })
            .AsNoTracking()
            .SingleOrDefaultAsync();

        if (imageData == null)
        {
            return NotFound();
        }

        // Simplified authorization check without loading full print entity
        var userId = User.GetUserId();
        if (imageData.PrintViewStatus == Print.PrintViewStatus.Private &&
            (!userId.HasValue || userId.Value != imageData.PrintCreatedById))
        {
            return Forbid();
        }

        try
        {
            var printImageDto = await printImageService.DownloadPrintFile(imageData.File);

            new FileExtensionContentTypeProvider().TryGetContentType(printImageDto.FileName!, out var contentType);
            return File(printImageDto.File!, contentType ?? "application/octet-stream");

        }
        catch (DoesNotExistException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Prints have a single "default" image, which is the image used in thumbnails. This is used to mark a specific image as the default image.
    /// </summary>
    /// <param name="printId">The id of the print.</param>
    /// <param name="imageId">The id of the image to make default.</param>
    /// <returns>Ok if the operation was successful.</returns>
    [HttpPost("{printId}/image/{imageId}/set-as-default")]
    public async Task<ActionResult> SetImageAsDefault(long printId, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var print = await printService.GetPrintById(printId);

        if (print == null || !print.Images!.Any(i => i.Id == imageId))
        {
            return NotFound();
        }

        // You can only change defaults for prints you created.
        if (userId != print.CreatedById)
        {
            return Forbid();
        }

        await printService.SetDefaultImage(printId, imageId);

        return Ok();
    }

    /// <summary>
    /// Reorder the images attached to a print by assigning a display order to each image.
    /// </summary>
    /// <remarks>
    /// This endpoint requires a <strong>complete</strong> set of image IDs for the print.
    /// Every image currently attached to the print must be included in <paramref name="reorderDto"/> —
    /// partial updates (supplying only a subset of IDs, or including extra IDs) are not supported.
    /// If the supplied set of image IDs does not exactly match the images belonging to the print,
    /// the request is rejected with a 400 Bad Request response.
    /// </remarks>
    /// <param name="printId">The id of the print whose images are being reordered.</param>
    /// <param name="reorderDto">
    /// The complete list of image IDs and their new display order values.
    /// Must contain every image ID that belongs to the print — no more, no less.
    /// </param>
    /// <response code="200">The images were successfully reordered.</response>
    /// <response code="400">
    /// Returned when the supplied image ID set does not exactly match the images attached to the print,
    /// or when the images list is null or empty.
    /// </response>
    /// <response code="403">Returned when the authenticated user does not own the requested print.</response>
    /// <response code="404">Returned when no print is found with the given <paramref name="printId"/>.</response>
    [HttpPut("{printId}/images/reorder")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ReorderImages(long printId, [FromBody] ReorderImagesDto reorderDto)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var print = await context.Prints
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == printId);

        if (print == null)
        {
            return NotFound();
        }

        if (print.CreatedById != userId)
        {
            return Forbid();
        }

        if (reorderDto.Images == null || reorderDto.Images.Count == 0)
        {
            return BadRequest("Images list cannot be null or empty");
        }

        // Validate all image IDs belong to this print
        var printImageIds = print.Images!.Select(i => i.Id).ToHashSet();
        var requestedIds = reorderDto.Images.Select(i => i.ImageId).ToHashSet();

        if (!requestedIds.SetEquals(printImageIds))
        {
            return BadRequest("Image IDs do not match print images");
        }

        // Update display order for each image
        foreach (var imageOrder in reorderDto.Images)
        {
            var image = print.Images!.First(i => i.Id == imageOrder.ImageId);
            image.DisplayOrder = imageOrder.DisplayOrder;
        }

        await context.SaveChangesAsync();

        cacheVersionService.InvalidateUserCache(userId.Value);

        return Ok();
    }

    /// <summary>
    /// Create a new image and attach it to an existing print. If the "isDefault" param is set to true, then
    /// it will mark the image as the print's default image.
    /// </summary>
    /// <param name="id">The ID of the print to save the image to.</param>
    /// <param name="image">The image file to save.</param>
    /// <param name="isDefault">If true, then mark the new image as the print's default image.</param>
    /// <returns>The created PrintImage with its ID.</returns>
    [HttpPost("{id}/image")]
    public async Task<ActionResult<PrintImageDto>> PostImage(long id, IFormFile image, [FromForm] bool isDefault = false)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var print = await printService.GetPrintById(id);

        if (print == null)
        {
            return NotFound();
        }

        // You can only upload images for prints you own.
        if (userId != print.CreatedById)
        {
            return Forbid();
        }

        if (image == null)
        {
            return BadRequest("Image file is required.");
        }

        var allowedImageTypes = new[] { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp" };
        if (!allowedImageTypes.Contains(image.ContentType.ToLowerInvariant()))
        {
            return BadRequest("Only image files are accepted (jpeg, png, gif, webp, bmp).");
        }

        const long maxImageSizeBytes = 10 * 1024 * 1024;
        if (image.Length > maxImageSizeBytes)
        {
            return BadRequest("Image must be under 10MB.");
        }

        // Check image limit
        var maxImages = await printService.GetMaxImagesPerPrint(userId.Value);
        var existingImageCount = await context.PrintImages.CountAsync(pi => pi.PrintId == id);
        if (existingImageCount >= maxImages)
        {
            return BadRequest($"Maximum of {maxImages} images per print allowed");
        }

        var fileId = Guid.NewGuid();
        var fileName = fileId + Path.GetExtension(image.FileName);



        using var uploadFileStream = image.OpenReadStream();
        var uploadResult = await blobStorageService.UploadAsync(printImageContainerName, fileName, uploadFileStream);

        var file = new Models.File()
        {
            Size = image.Length,
            Path = uploadResult.BlobPath,
            Id = fileId,
            CreatedById = userId.Value,
            UpdatedById = userId.Value,
        };
        context.Files.Add(file);

        // Calculate next display order
        var maxDisplayOrder = await context.PrintImages
            .Where(pi => pi.PrintId == id)
            .MaxAsync(pi => (int?)pi.DisplayOrder) ?? -1;

        var printImage = new PrintImage()
        {
            File = file,
            CreatedById = userId.Value,
            UpdatedById = userId.Value,
            Print = print,
            IsDefault = isDefault,
            DisplayOrder = maxDisplayOrder + 1,
        };
        context.PrintImages.Add(printImage);

        if (isDefault)
        {
            // Set other defaults to false;
            var otherEntities = await context.PrintImages.Where(p => p.PrintId == id && p.IsDefault == true && p.FileId != fileId).ToListAsync();
            otherEntities.ForEach(p => p.IsDefault = false);
        }

        await context.SaveChangesAsync();

        telemetry.TrackEvent("PrintPictureAdded");

        // Return the created image with its ID so the client can use it for reordering
        var printImageDto = new PrintImageDto
        {
            Id = printImage.Id,
            IsDefault = printImage.IsDefault,
            DisplayOrder = printImage.DisplayOrder
        };

        return CreatedAtAction("GetImage", new { printId = id, imageId = printImage.Id }, printImageDto);
    }

    /// <summary>
    /// Delete an image from a print. If the deleted image was the default, the next image by DisplayOrder is promoted.
    /// </summary>
    /// <param name="printId">The id of the print.</param>
    /// <param name="imageId">The id of the image to remove.</param>
    /// <returns></returns>
    [HttpDelete("{printId}/image/{imageId}")]
    public async Task<ActionResult> RemoveImage(long printId, int imageId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var print = await context.Prints
            .Include(p => p.Images)
            .FirstOrDefaultAsync(p => p.Id == printId);

        if (print == null)
        {
            return NotFound();
        }

        if (print.CreatedById != userId)
        {
            return Forbid();
        }

        var imageToDelete = print.Images!.FirstOrDefault(i => i.Id == imageId);
        if (imageToDelete == null)
        {
            return NotFound("Image not found");
        }

        var wasDefault = imageToDelete.IsDefault;

        context.PrintImages.Remove(imageToDelete);

        // If deleted image was default, promote next image by DisplayOrder
        if (wasDefault)
        {
            var nextDefault = print.Images!
                .Where(i => i.Id != imageId)
                .OrderBy(i => i.DisplayOrder)
                .FirstOrDefault();

            if (nextDefault != null)
            {
                nextDefault.IsDefault = true;
            }
        }

        await context.SaveChangesAsync();

        cacheVersionService.InvalidateUserCache(userId.Value);

        return Ok();
    }

    /// <summary>
    /// Create a new comment and attach it to the print.
    /// </summary>
    /// <param name="printId">The ID of the print to comment on.</param>
    /// <param name="newComment">The details of the new comment.</param>
    /// <response code="200">An OK response with the new comment details if the comment was added successfully.</response>
    /// <response code="400">Returned if the request contains bad details, or if the print specified does not allow comments.</response>
    /// <response code="403">Returned if the user cannot access the print specified.</response>
    [HttpPost("{printId}/comment")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "BindRequired used.")]
    public async Task<ActionResult<CommentDetailDto>> PostPrintComment(long printId, [FromBody, BindRequired] AddCommentDto newComment)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var print = await printService.GetPrintById(printId);

        if (print == null)
        {
            return NotFound();
        }

        // Validation for adding new comments.
        if (!print.AllowComments)
        {
            return BadRequest("Comments are disabled for this print.");
        }

        // Only the original creator should be able to comment on private prints.
        if (print.ViewStatus == Print.PrintViewStatus.Private && userId != print.CreatedById)
        {
            return Forbid();
        }

        var comment = await printService.AddPrintComment(print, newComment.Body!, userId.Value);

        // Null-forgiven: the comment was just persisted, so the re-read always finds it.
        var mappedComment = await commentService.GetCommentDetailById(comment.Id);

        return mappedComment!;
    }

    /// <summary>
    /// Delete a Comment from a print. The owner of the Print can remove any comment on that print, while other users can only delete comments they created.
    /// </summary>
    /// <param name="printId">The print ID</param>
    /// <param name="commentId">The comment Id to delete.</param>
    /// <response code="200">An OK response if the deletion was successful.</response>
    /// <response code="403">Returned if the user is not the print owner or the owner of the comment.</response>
    [HttpDelete("{printId}/comment/{commentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommentDetailDto>> DeletePrintComment(long printId, long commentId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var print = await printService.GetPrintById(printId);

        if (print == null)
        {
            return NotFound("Print not found.");
        }

        // Check if print contains the print comment selected.
        var printComment = print.Comments!.Where(pc => pc.CommentId == commentId).SingleOrDefault();

        if (printComment is null)
        {
            return NotFound("Comment not found.");
        }

        // User can delete the print if they own the print, or if they made the comment:
        var userOwnsPrint = print.CreatedById == userId.Value;
        var userOwnsComment = printComment.Comment.CreatedById == userId.Value;

        if (!(userOwnsPrint || userOwnsComment))
        {
            return BadRequest("Cannot remove other's comments on other's prints.");
        }

        // Delete the print and comment:
        await commentService.DeleteCommentById(commentId);

        return Ok();
    }

    /// <summary>
    /// Returns an array of all the IDs for public prints, for use with creating and updating sitemaps.
    /// </summary>
    /// <returns></returns>
    [AllowAnonymous]
    [HttpGet("public")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<ActionResult<IEnumerable<long>>> GetPublicPrintIds()
    {
        telemetry.TrackEvent("PublicPrintsQueried");
        return await printService.GetPublicPrintIds();
    }

    /// <summary>
    /// Generates a SAS URL for uploading a file directly to blob storage. Pro subscription required.
    /// </summary>
    /// <param name="id">The ID of the print to attach the file to.</param>
    /// <param name="request">Details about the file to be uploaded.</param>
    /// <response code="200">Returns the SAS upload URL and blob path.</response>
    /// <response code="400">If the file type is not supported or quota is exceeded.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user does not own the print or does not have a Pro subscription.</response>
    /// <response code="404">If the print does not exist.</response>
    [HttpPost("{id}/files/upload-url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GetUploadUrlResponse>> GetFileUploadUrl(
        long id, [FromBody] GetUploadUrlRequest request)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var result = await fileAttachmentService.GetUploadUrlAsync(id, userId.Value, request);
            return Ok(result);
        }
        catch (NotFoundException) { return NotFound(); }
        catch (ForbiddenException) { return Forbid(); }
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
    }

    /// <summary>
    /// Confirms that a file has been uploaded to blob storage and records it as an attachment on the print.
    /// Pro subscription required.
    /// </summary>
    /// <param name="id">The ID of the print the file was uploaded for.</param>
    /// <param name="request">The blob path and file metadata returned from the upload-url step.</param>
    /// <response code="200">Returns the created PrintAttachment details.</response>
    /// <response code="400">If the blob path is invalid.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user does not own the print or does not have a Pro subscription.</response>
    /// <response code="404">If the print does not exist.</response>
    [HttpPost("{id}/files/confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintAttachmentDto>> ConfirmFileUpload(
        long id, [FromBody] ConfirmUploadRequest request)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            var result = await fileAttachmentService.ConfirmUploadAsync(id, userId.Value, request);
            return Ok(result);
        }
        catch (NotFoundException) { return NotFound(); }
        catch (ForbiddenException) { return Forbid(); }
        catch (BadRequestException ex) { return BadRequest(ex.Message); }
    }

    /// <summary>
    /// Returns all file attachments for a print.
    /// Anonymous access is permitted for public prints; private prints require the owner to be authenticated.
    /// </summary>
    /// <param name="id">The ID of the print whose attachments to retrieve.</param>
    /// <response code="200">Returns the list of file attachments (may be empty).</response>
    /// <response code="403">If the print is private and the requesting user is not the owner.</response>
    /// <response code="404">If the print does not exist.</response>
    [HttpGet("{id}/files")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<PrintAttachmentDto>>> GetFiles(long id)
    {
        // Load minimal print data needed to check visibility — mirrors the GetImage pattern.
        var printData = await context.Prints
            .Where(p => p.Id == id)
            .Select(p => new { p.ViewStatus, p.CreatedById })
            .AsNoTracking()
            .SingleOrDefaultAsync();

        if (printData == null)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        if (printData.ViewStatus == Print.PrintViewStatus.Private &&
            (!userId.HasValue || userId.Value != printData.CreatedById))
        {
            return Forbid();
        }

        var files = await fileAttachmentService.GetFilesAsync(id);
        return Ok(files);
    }

    /// <summary>
    /// Generates a time-limited SAS download URL for a file attachment.
    /// Anonymous access is permitted when the print has file downloads enabled; otherwise the owner must be authenticated.
    /// </summary>
    /// <param name="id">The ID of the print the file is attached to.</param>
    /// <param name="fileId">The ID of the attachment to download.</param>
    /// <response code="200">Returns the download URL and its expiry time.</response>
    /// <response code="403">If file downloads are not enabled for this print and the user is not the owner.</response>
    /// <response code="404">If the print or attachment does not exist.</response>
    [HttpGet("{id}/files/{fileId}/download-url")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GetDownloadUrlResponse>> GetFileDownloadUrl(long id, long fileId)
    {
        var userId = User.GetUserId(); // null if anonymous — that's ok

        try
        {
            var result = await fileAttachmentService.GetDownloadUrlAsync(id, fileId, userId);
            return Ok(result);
        }
        catch (NotFoundException) { return NotFound(); }
        catch (ForbiddenException) { return Forbid(); }
    }

    /// <summary>
    /// Deletes a file attachment from a print. Only the owner of the attachment may delete it.
    /// </summary>
    /// <param name="id">The ID of the print the file is attached to.</param>
    /// <param name="fileId">The ID of the attachment to delete.</param>
    /// <response code="200">If the file was deleted successfully.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user does not own the file attachment.</response>
    /// <response code="404">If the print or attachment does not exist.</response>
    [HttpDelete("{id}/files/{fileId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteFile(long id, long fileId)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        try
        {
            await fileAttachmentService.DeleteFileAsync(id, fileId, userId.Value);
            return Ok();
        }
        catch (NotFoundException) { return NotFound(); }
        catch (ForbiddenException) { return Forbid(); }
    }

    /// <summary>
    /// Helper method to  check if the current user can view print
    /// </summary>
    /// <param name="print"></param>
    /// <returns></returns>
    private async Task<bool> CanViewPrint(Print print)
    {
        var authorizationResult = await authorizationService
                        .AuthorizeAsync(User, print, "ViewPrint");

        return authorizationResult.Succeeded;

    }

    private bool PrintExists(long id)
    {
        return context.Prints.Any(e => e.Id == id);
    }

    /// <summary>
    /// Generates a unique cache key for print summary queries based on user and query parameters.
    /// </summary>
    private string GenerateCacheKey(long userId, long? currentUserId, string version,
                                    PagedRequest pagingRequest, string? searchText,
                                    IEnumerable<long> filterByPrinterIds,
                                    IEnumerable<Guid> filterByFilamentIds,
                                    SortRequest<PrintSummarySortColumn> sortRequest,
                                    IReadOnlyCollection<Print.PrintStatus> statuses,
                                    IReadOnlyCollection<Guid> projectIds,
                                    DateTimeOffset? fromDate,
                                    DateTimeOffset? toDate)
    {
        var printerIds = filterByPrinterIds?.Any() == true
            ? string.Join(",", filterByPrinterIds.OrderBy(x => x))
            : "none";

        var filamentIds = filterByFilamentIds?.Any() == true
            ? string.Join(",", filterByFilamentIds.OrderBy(x => x))
            : "none";

        var viewerKey = currentUserId.HasValue ? currentUserId.Value.ToString() : "anon";

        // EVERY filter must reach the key. This action is [AllowAnonymous] and serves public
        // profiles, so a filter that is applied to the query but omitted from the key lets one
        // request's result be served to a different request — a July query returning June's
        // rows, cross-viewer. Collections are sorted so ?a=1&a=2 and ?a=2&a=1 share an entry,
        // and "none" keeps an empty collection from rendering as "" and colliding with a value.
        var statusKey = statuses?.Count > 0
            ? string.Join(",", statuses.Select(s => s.ToString()).OrderBy(x => x, StringComparer.Ordinal))
            : "none";

        var projectKey = projectIds?.Count > 0
            ? string.Join(",", projectIds.OrderBy(x => x))
            : "none";

        // Round-trip format: two instants differing by a tick must produce different keys.
        var fromKey = fromDate?.ToUniversalTime().ToString("O") ?? "none";
        var toKey = toDate?.ToUniversalTime().ToString("O") ?? "none";

        return $"{PRINT_SUMMARY_CACHE_PREFIX}{userId}_viewer{viewerKey}_v{version}_" +
               $"p{pagingRequest.PageNumber}_s{pagingRequest.PageSize}_" +
               $"q{searchText ?? "none"}_" +
               $"pr{printerIds}_" +
               $"fl{filamentIds}_" +
               $"st{sortRequest?.SortColumn}_{sortRequest?.SortDirection}_" +
               $"fs{statusKey}_" +
               $"fp{projectKey}_" +
               $"df{fromKey}_dt{toKey}";
    }

    /// <summary>
    /// Estimates the cache size for a paged list result in cache size units (approximate KB).
    /// </summary>
    private long EstimateCacheSize(PagedList<PrintSummaryDTO> result)
    {
        // Rough estimate: ~2KB per print summary item + overhead
        return (result?.Items?.Count ?? 0) * 2;
    }
}
