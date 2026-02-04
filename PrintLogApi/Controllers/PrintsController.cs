using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Exceptions;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Operations involving Prints, print filament, print images, and print comments.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class PrintsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly IAuthorizationService _authorizationService;
        private readonly TelemetryClient _telemetry;
        private readonly IPrintService _printService;
        private readonly ICommentService _commentService;
        private readonly IPrintImageService _printImageService;
        private readonly IMemoryCache _cache;
        private readonly ICacheVersionService _cacheVersionService;
        private readonly string printImageContainerName = "printimages";
        private readonly BlobContainerClient printImageContainer;
        private const string PRINT_SUMMARY_CACHE_PREFIX = "print_summary_";

        public PrintsController(
            PrintLogContext context,
            IMapper mapper,
            IConfiguration config,
            IAuthorizationService authorizationService,
            TelemetryClient telemetry,
            IPrintService printService,
            IPrintImageService printImageService,
            ICommentService commentService,
            IMemoryCache cache,
            ICacheVersionService cacheVersionService)
        {
            _context = context;
            _mapper = mapper;
            _authorizationService = authorizationService;
            _telemetry = telemetry;
            _printService = printService;
            _commentService = commentService;
            _printImageService = printImageService;
            _cache = cache;
            _cacheVersionService = cacheVersionService;

            var blobServiceClient = new BlobServiceClient(config["AZURE_STORAGE_CONNECTION_STRING"]);
            printImageContainer = blobServiceClient.GetBlobContainerClient(printImageContainerName);
        }

        /// <summary>
        ///     Get a paged list of Print Summary information for a user. 
        ///     If no userId is provided, then all prints for the currently authenticated user will be queried.
        ///     Otherwise, if a userId is provided, then only the user's public prints will be returned.
        /// </summary>
        /// <param name="pagingRequest">The paging request.</param>
        /// <param name="searchText">Optionally search for text in a print's title or notes.</param>
        /// <param name="filterByPrinterIds">Optionally filter by specific printer ids.</param>
        /// <param name="sortRequest">The sorting request.</param>
        /// <param name="filterByStatus">Optionally filter by a specific print status. <see cref="PrintStatus"/></param>
        /// <param name="userId">Optionally search for public</param>
        /// <returns>A Paged List of Print Summaries matching the search criteria.</returns>
        /// <response code="200">A Paged List of Print Summaries matching the search criteria.</response>
        /// <response code="400">Returned if no user is logged in, and no userId is provided.</response>
        [HttpGet("summary")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<PagedList<PrintSummaryDTO>>> GetPrintSummary(
            [FromQuery] PagedRequest pagingRequest,
            [FromQuery, MaxLength(50)] string searchText,
            [FromQuery] IEnumerable<long> filterByPrinterIds,
            [FromQuery] SortRequest<PrintSummarySortColumn> sortRequest,
            [FromQuery] Print.PrintStatus? filterByStatus,
            [FromQuery] long? userId)
        {

            long? currentUserId = User.GetUserId();

            if (!userId.HasValue && userId != currentUserId && !currentUserId.HasValue)
            {
                return BadRequest("User is not logged in, and summary is not filtered by a specific userId. Please log in and try again.");
            }

            var targetUserId = userId ?? currentUserId.Value;
            var version = _cacheVersionService.GetUserCacheVersion(targetUserId);
            var cacheKey = GenerateCacheKey(targetUserId, version, pagingRequest, searchText, 
                                            filterByPrinterIds, sortRequest, filterByStatus);

            if (_cache.TryGetValue(cacheKey, out PagedList<PrintSummaryDTO> cachedResult))
            {
                return cachedResult;
            }

            var result = await _printService.SearchPrintSummary(pagingRequest, searchText, sortRequest, filterByPrinterIds, filterByStatus, userId, currentUserId);

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetSize(EstimateCacheSize(result))
                .SetSlidingExpiration(TimeSpan.FromMinutes(5))
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(15))
                .SetPriority(CacheItemPriority.Normal);

            _cache.Set(cacheKey, result, cacheOptions);

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

            var printStats = await _printService.GetPrintStatisticsForUser(userId.Value, fromDate, toDate);

            return printStats;
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
            var print = await _printService.GetPrintById(id);

            if (print == null)
            {
                return NotFound();
            }

            if (!await CanViewPrint(print))
            {
                return Forbid();
            }

            var printDetailDto = await this._context.Prints
                .Include(p => p.Printer)
                .Include(p => p.Images)
                    .ThenInclude(p => p.File)
                .Include(p => p.Comments)
                    .ThenInclude(p => p.Comment)
                .Include(p => p.FilamentUsage)
                    .ThenInclude(pf => pf.Filament)
                .Where(p => p.Id == id)
                .AsNoTracking()
                .ProjectTo< PrintDetailDTO>(_mapper.ConfigurationProvider)
                .FirstOrDefaultAsync();

            printDetailDto.Comments = printDetailDto.Comments.OrderBy(c => c.CreatedDate).ToList();
            
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
        public async Task<IActionResult> GetAllPrintDetailsAsCsv()
        {
            long? currentUserId = User.GetUserId();

            if (!currentUserId.HasValue)
            {
                return Unauthorized();
            }

            var stream = await _printService.GeneratePrintReportAsCsvForUser(currentUserId.Value);

            return File(stream, "application/octet-stream", "PrintReports.csv");
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
                return BadRequest();
            }

            var existingPrint = await _printService.GetPrintById(id);

            if (existingPrint == null)
            {
                return NotFound();
            }

            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            if (userId != existingPrint.CreatedById || userId != existingPrint.Printer.UserId)
            {
                return Forbid();
            }

            try
            {
                var updatedPrint = await _printService.UpdatePrint(id, printDTO, userId.Value);

                _cacheVersionService.InvalidateUserCache(userId.Value);

                return CreatedAtAction("GetPrintById", new { id = existingPrint.Id }, _mapper.Map<PrintDetailDTO>(updatedPrint));
            } catch (UserCannotAccessPrinterException)
            {
                return BadRequest();
            } catch (DoesNotExistException)
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

            var existingPrint = await _printService.GetPrintById(id);

            if (existingPrint == null)
            {
                return NotFound();
            }

            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }
            if ( userId != existingPrint.CreatedById || userId != existingPrint.Printer.UserId)
            {
                return Forbid();
            }

            try
            {
                var updatedPrint = await _printService.UpdatePrintStatus(id, newStatus, userId.Value);
                
                _cacheVersionService.InvalidateUserCache(userId.Value);
                
                return CreatedAtAction("GetPrintById", new { id = existingPrint.Id }, _mapper.Map<PrintDetailDTO>(existingPrint));
            } catch (DoesNotExistException)
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
                var newPrint = await _printService.AddPrint(print, userId.Value);
                _telemetry.TrackEvent("PrintAdded");

                _cacheVersionService.InvalidateUserCache(userId.Value);

                return CreatedAtAction("GetPrintById", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
            } catch (UserCannotAccessPrinterException)
            {
                return BadRequest("Selected printer does not belong to currently logged in user.");
            } catch (UserCannotAccessFilamentException)
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

            var existingPrint = await _printService.GetPrintById(id);

            if (existingPrint == null)
            {
                return NotFound();
            }


            if (userId != existingPrint.CreatedById)
            {
                return Forbid();
            }

            await _printService.DeletePrint(existingPrint);

            _cacheVersionService.InvalidateUserCache(userId.Value);

            var properties = new Dictionary<string, string> { 
                { "PrintId", existingPrint.Id.ToString() }, 
                { "UserId", userId.ToString() }, 
                { "PrintCreated", existingPrint.CreatedDate.ToString("O", CultureInfo.InvariantCulture) }  
            };
            _telemetry.TrackEvent("PrintDeleted", properties);

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
        public async Task<IActionResult> GetImage(long printId, int imageId)
        {
            // Optimized query: only load the specific image and minimal print data needed for authorization
            var imageData = await _context.PrintImages
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
                var printImageDto = await _printImageService.DownloadPrintFile(imageData.File);

                new FileExtensionContentTypeProvider().TryGetContentType(printImageDto.FileName, out var contentType);
                return File(printImageDto.File, contentType ?? "application/octet-stream");

            } catch (DoesNotExistException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Prints have a single "default" image, which is the image used in thumbnails. This is used to mark a specific image as the default image.
        /// </summary>
        /// <param name="printid">The id of the print.</param>
        /// <param name="imageId">The id of the image to make default.</param>
        /// <returns>Ok if the operation was successful.</returns>
        [HttpPost("{printid}/image/{imageId}/set-as-default")]
        public async Task<ActionResult> SetImageAsDefault(long printid, int imageId)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var print = await _printService.GetPrintById(printid);

            if (print == null || !print.Images.Any(i => i.Id == imageId))
            {
                return NotFound();
            }

            // You can only change defaults for prints you created.
            if (userId != print.CreatedById)
            {
                return Forbid();
            }

            await _printService.SetDefaultImage(printid, imageId);
            
            return Ok();

            //return CreatedAtAction("GetPrintById", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
        }

        /// <summary>
        /// Reorder images attached to a print.
        /// </summary>
        /// <param name="printId">The id of the print.</param>
        /// <param name="reorderDto">The new image ordering.</param>
        /// <returns>Ok if the operation was successful.</returns>
        [HttpPut("{printId}/images/reorder")]
        public async Task<ActionResult> ReorderImages(long printId, [FromBody] ReorderImagesDto reorderDto)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var print = await _context.Prints
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

            // Validate all image IDs belong to this print
            var printImageIds = print.Images.Select(i => i.Id).ToHashSet();
            var requestedIds = reorderDto.Images.Select(i => i.ImageId).ToHashSet();

            if (!requestedIds.SetEquals(printImageIds))
            {
                return BadRequest("Image IDs do not match print images");
            }

            // Update display order for each image
            foreach (var imageOrder in reorderDto.Images)
            {
                var image = print.Images.First(i => i.Id == imageOrder.ImageId);
                image.DisplayOrder = imageOrder.DisplayOrder;
            }

            await _context.SaveChangesAsync();

            return Ok();
        }

        /// <summary>
        /// Create a new image and attach it to an existing print. If the "isDefault" param is set to true, then
        /// it will mark the image as the print's default image.
        /// </summary>
        /// <param name="id">The ID of the print to save the image to.</param>
        /// <param name="image">The image file to save.</param>
        /// <param name="isDefault">If true, then mark the new image as the print's default image.</param>
        /// <returns></returns>
        [HttpPost("{id}/image")]
        public async Task<ActionResult> PostImage(long id, IFormFile image, [FromForm] bool isDefault = false)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var print = await _printService.GetPrintById(id);

            if (print == null)
            {
                return NotFound();
            }

            // You can only upload images for prints you own.
            if (userId != print.CreatedById)
            {
                return Forbid();
            }

            // Check image limit (max 5 images per print)
            var existingImageCount = await _context.PrintImages.CountAsync(pi => pi.PrintId == id);
            if (existingImageCount >= 5)
            {
                return BadRequest("Maximum of 5 images per print allowed");
            }

            //foreach (IFormFile image in images)
            //{
            var fileId = Guid.NewGuid();
            var fileName = fileId + Path.GetExtension(image.FileName);



            var blobClient = printImageContainer.GetBlobClient(fileName);

            using (var uploadFileStream = image.OpenReadStream())
            {
                await blobClient.UploadAsync(uploadFileStream);
            };

            var file = new Models.File()
            {
                Size = image.Length,
                Path = $"{printImageContainerName}/{fileName}",
                Id = fileId,
                CreatedById = userId.Value,
                UpdatedById = userId.Value,
            };
            _context.Files.Add(file);

            // Calculate next display order
            var maxDisplayOrder = await _context.PrintImages
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
            _context.PrintImages.Add(printImage);

            //}

            if (isDefault)
            {
                // Set other defaults to false;
                var otherEntities = await _context.PrintImages.Where(p => p.PrintId == id && p.IsDefault == true && p.FileId != fileId).ToListAsync();
                otherEntities.ForEach(p => p.IsDefault = false);
            }

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent("PrintPictureAdded");

            return Ok();

            //return CreatedAtAction("GetPrintById", new { id = newPrint.Id }, _mapper.Map<PrintDetailDTO>(newPrint));
        }

        /// <summary>
        /// Delete an image from a print. If the deleted image was the default, the next image by DisplayOrder is promoted.
        /// </summary>
        /// <param name="printid">The id of the print.</param>
        /// <param name="imageId">The id of the image to remove.</param>
        /// <returns></returns>
        [HttpDelete("{printid}/image/{imageId}")]
        public async Task<ActionResult> RemoveImage(long printid, int imageId)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var print = await _context.Prints
                .Include(p => p.Images)
                .FirstOrDefaultAsync(p => p.Id == printid);

            if (print == null)
            {
                return NotFound();
            }

            if (print.CreatedById != userId)
            {
                return Forbid();
            }

            var imageToDelete = print.Images.FirstOrDefault(i => i.Id == imageId);
            if (imageToDelete == null)
            {
                return NotFound("Image not found");
            }

            var wasDefault = imageToDelete.IsDefault;

            _context.PrintImages.Remove(imageToDelete);

            // If deleted image was default, promote next image by DisplayOrder
            if (wasDefault)
            {
                var nextDefault = print.Images
                    .Where(i => i.Id != imageId)
                    .OrderBy(i => i.DisplayOrder)
                    .FirstOrDefault();

                if (nextDefault != null)
                {
                    nextDefault.IsDefault = true;
                }
            }

            await _context.SaveChangesAsync();

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

            var print = await _printService.GetPrintById(printId);

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

            var comment = await _printService.AddPrintComment(print, newComment.Body, userId.Value);

            var mappedComment = await _commentService.GetCommentDetailById(comment.Id);

            return mappedComment;
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

            var print = await _printService.GetPrintById(printId);

            if (print == null)
            {
                return NotFound("Print not found.");
            }

            // Check if print contains the print comment selected.
            var printComment = print.Comments.Where(pc => pc.CommentId == commentId).SingleOrDefault();

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
            await _commentService.DeleteCommentById(commentId);

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
            this._telemetry.TrackEvent("PublicPrintsQueried");
            return await this._printService.GetPublicPrintIds();
        }

        /// <summary>
        /// Helper method to  check if the current user can view print
        /// </summary>
        /// <param name="print"></param>
        /// <returns></returns>
        private async Task<bool> CanViewPrint(Print print)
        {
            var authorizationResult = await _authorizationService
                            .AuthorizeAsync(User, print, "ViewPrint");

            return authorizationResult.Succeeded;

        }

        private bool PrintExists(long id)
        {
            return _context.Prints.Any(e => e.Id == id);
        }

        /// <summary>
        /// Generates a unique cache key for print summary queries based on user and query parameters.
        /// </summary>
        private string GenerateCacheKey(long userId, string version, 
                                        PagedRequest pagingRequest, string searchText,
                                        IEnumerable<long> filterByPrinterIds, 
                                        SortRequest<PrintSummarySortColumn> sortRequest,
                                        Print.PrintStatus? filterByStatus)
        {
            var printerIds = filterByPrinterIds?.Any() == true 
                ? string.Join(",", filterByPrinterIds.OrderBy(x => x)) 
                : "none";
            
            return $"{PRINT_SUMMARY_CACHE_PREFIX}{userId}_v{version}_" +
                   $"p{pagingRequest.PageNumber}_s{pagingRequest.PageSize}_" +
                   $"q{searchText ?? "none"}_" +
                   $"pr{printerIds}_" +
                   $"st{sortRequest?.SortColumn}_{sortRequest?.SortDirection}_" +
                   $"fs{filterByStatus}";
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
}
