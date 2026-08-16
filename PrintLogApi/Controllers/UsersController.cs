using System.Globalization;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.User;
using PrintLogApi.Services;
using PrintLogApi.Users;

namespace PrintLogApi.Controllers;

/// <summary>
/// Manage 3D Print Log Users.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class UsersController(
    PrintLogContext context,
    IUserDeletionService userDeletionService,
    IUserService userService,
    IMapper mapper,
    IAuthorizationService authorizationService,
    TelemetryClient telemetry,
    IBlobStorageService blobStorageService,
    ISubscriptionService subscriptionService) : ControllerBase
{
    private readonly string profileImageContainerName = "userprofile";

    private static readonly string[] AllowedImageContentTypes = { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp" };
    private const long MaxImageSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Get the user summary for the specified user id. 
    /// </summary>
    /// <param name="id">The ID of the user to retrieve.</param>
    /// <returns></returns>
    [HttpGet("{id}/summary")]
    [AllowAnonymous]
    public async Task<ActionResult<UserSummaryDto>> GetCurrentUserDetails(long id)
    {
        var user = await context.Users
            .Where(u => u.Id == id)
            .ProjectTo<UserSummaryDto>(mapper.ConfigurationProvider)
            .AsNoTracking()
            .SingleOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        return user;
    }

    /// <summary>
    /// Gets the user details for the currently authenticated user.
    /// </summary>
    /// <returns></returns>
    [HttpGet("me")]
    public async Task<ActionResult<UserDetailDto>> GetCurrentUserDetails()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var user = await context.Users
            .Where(u => u.Id == userId)
            .ProjectTo<UserDetailDto>(mapper.ConfigurationProvider)
            .AsNoTracking()
            .SingleAsync();

        return user;
    }

    /// <summary>
    /// Marks the current user as deactivated
    /// </summary>
    /// <returns></returns>
    [HttpPost("me/deactivate")]
    public async Task<ActionResult<UserDetailDto>> DeactivateCurrentUser()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        await userService.MarkUserAsDeactivated(userId.Value);
        await subscriptionService.CancelSubscriptionAtPeriodEnd(userId.Value);

        telemetry.TrackEvent("UserDeactivated", new Dictionary<string, string>() { { "UserId", userId.Value.ToString(CultureInfo.InvariantCulture) } });

        var user = await context.Users
            .Where(u => u.Id == userId)
            .ProjectTo<UserDetailDto>(mapper.ConfigurationProvider)
            .AsNoTracking()
            .SingleAsync();

        return user;
    }

    /// <summary>
    ///  Reactivate the current user if the user has not yet been deleted.
    /// </summary>
    /// <returns></returns>
    [HttpPost("me/reactivate")]
    public async Task<ActionResult<UserDetailDto>> ReactivateCurrentUser()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        await userService.ReactivateUser(userId.Value);
        await subscriptionService.ResumeSubscription(userId.Value);

        telemetry.TrackEvent("UserReactivated", new Dictionary<string, string>() { { "UserId", userId.Value.ToString(CultureInfo.InvariantCulture) } });


        var user = await context.Users
            .Where(u => u.Id == userId)
            .ProjectTo<UserDetailDto>(mapper.ConfigurationProvider)
            .AsNoTracking()
            .SingleAsync();

        return user;
    }

    /// <summary>
    ///   Delete the data from users pending deactivation after the deactivation period.
    /// </summary>
    /// <returns></returns>
    [HttpDelete("pending-deactivation")]
    [AllowAnonymous]
    public async Task<ActionResult> ProcessPendingDeactivations()
    {
        await userDeletionService.DeletePendingDeactivatedUsers();

        return Ok();
    }

    /// <summary>
    /// Gets the user details of the specified user. Respects the user's Profile View Status, so private profiles cannot be accessed by other users.
    /// </summary>
    /// <param name="id">The Id of the user to retrieve.</param>
    /// <returns></returns>
    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<ActionResult<UserDetailDto>> GetUserDetails(long id)
    {
        var user = await context.Users
        .Where(u => u.Id == id)
        .AsNoTracking()
        .SingleOrDefaultAsync();

        if (user == null)
        {
            return NotFound();
        }

        if (!await CanViewUserProfile(user))
        {
            return Forbid();
        }

        return mapper.Map<UserDetailDto>(user);
    }

    /// <summary>
    /// Updates the current user's details.
    /// </summary>
    /// <param name="updatedUser"></param>
    /// <returns></returns>
    [HttpPut("me")]
    public async Task<ActionResult<UserDetailDto>> UpdateCurrentUserDetails(UpdateUserDetailDto updatedUser)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var existingUser = await context.Users
            .Where(u => u.Id == userId)
            .SingleAsync();

        if (existingUser == null)
        {
            return NotFound();
        }


        existingUser = mapper.Map(updatedUser, existingUser);

        context.Entry(existingUser).State = EntityState.Modified;

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!UserExists(userId.Value))
            {
                return NotFound();
            }
            else
            {
                throw;
            }
        }

        return mapper.Map<UserDetailDto>(existingUser);
    }

    /// <summary>
    /// Replace the user profile image of the currently authenticated user.
    /// </summary>
    /// <param name="image">The image file to save.</param>
    /// <returns></returns>
    [HttpPost("me/profile-image")]
    public async Task<ActionResult<UserUrlDto>> PostProfileImage(IFormFile image)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var user = await context.Users.FindAsync(userId);

        if (user == null)
        {
            return NotFound();
        }

        if (image == null)
        {
            return BadRequest("Image file is required.");
        }

        if (!AllowedImageContentTypes.Contains(image.ContentType.ToLowerInvariant()))
        {
            return BadRequest("Only image files are accepted (jpeg, png, gif, webp, bmp).");
        }

        if (image.Length > MaxImageSizeBytes)
        {
            return BadRequest("Image must be under 10MB.");
        }

        var fileId = Guid.NewGuid();
        var fileName = fileId + Path.GetExtension(image.FileName);

        using (var uploadFileStream = image.OpenReadStream())
        {
            var uploadResult = await blobStorageService.UploadAsync(profileImageContainerName, fileName, uploadFileStream);

            var file = new Models.File()
            {
                Size = image.Length,
                Path = uploadResult.BlobPath,
                Id = fileId,
                CreatedById = userId.Value,
                UpdatedById = userId.Value,
            };
            context.Files.Add(file);

            user.ProfilePicture = uploadResult.BlobUri.AbsoluteUri;
        }

        await context.SaveChangesAsync();
        telemetry.TrackEvent("UserProfilePictureUploaded");

        return new UserUrlDto() { Url = user.ProfilePicture };
    }

    /// <summary>
    /// Replace the user cover image of the currently authenticated user.
    /// </summary>
    /// <param name="image">The image file to save.</param>
    /// <returns></returns>
    [HttpPost("me/cover-image")]
    public async Task<ActionResult<UserUrlDto>> PostCoverImage(IFormFile image)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var user = await context.Users.FindAsync(userId);

        if (user == null)
        {
            return NotFound();
        }

        if (image == null)
        {
            return BadRequest("Image file is required.");
        }

        if (!AllowedImageContentTypes.Contains(image.ContentType.ToLowerInvariant()))
        {
            return BadRequest("Only image files are accepted (jpeg, png, gif, webp, bmp).");
        }

        if (image.Length > MaxImageSizeBytes)
        {
            return BadRequest("Image must be under 10MB.");
        }

        var fileId = Guid.NewGuid();
        var fileName = fileId + Path.GetExtension(image.FileName);

        using (var uploadFileStream = image.OpenReadStream())
        {
            var uploadResult = await blobStorageService.UploadAsync(profileImageContainerName, fileName, uploadFileStream);

            var file = new Models.File()
            {
                Size = image.Length,
                Path = uploadResult.BlobPath,
                Id = fileId,
                CreatedById = userId.Value,
                UpdatedById = userId.Value,
            };
            context.Files.Add(file);

            user.CoverPicture = uploadResult.BlobUri.AbsoluteUri;
        }

        await context.SaveChangesAsync();
        telemetry.TrackEvent("UserCoverPictureUploaded");

        return new UserUrlDto() { Url = user.CoverPicture };
    }

    /// <summary>
    /// Remove the current user's cover-image.
    /// </summary>
    /// <returns></returns>
    [HttpDelete("me/cover-image")]
    public async Task<ActionResult<UserUrlDto>> RemoveCoverImage()
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var user = await context.Users.FindAsync(userId);

        if (user == null)
        {
            return NotFound();
        }

        user.CoverPicture = null;

        await context.SaveChangesAsync();

        return Ok();
    }

    /// <summary>
    /// Returns an array of all the IDs for public users, for use with creating and updating sitemaps.
    /// </summary>
    /// <returns></returns>
    [AllowAnonymous]
    [HttpGet("public")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<ActionResult<IEnumerable<long>>> GetPublicUserIds()
    {
        telemetry.TrackEvent("PublicUsersQueried");
        return await context.Users.Where(u => u.ViewStatus == Models.User.ProfileViewStatus.Public).Select(u => u.Id).ToListAsync();
    }

    /// <summary>
    /// Helper method to check if the current user can view the user profile
    /// </summary>
    /// <param name="profileToView"></param>
    /// <returns></returns>
    private async Task<bool> CanViewUserProfile(User profileToView)
    {
        var authorizationResult = await authorizationService
                        .AuthorizeAsync(User, profileToView, "ViewUserProfile");

        return authorizationResult.Succeeded;

    }

    private bool UserExists(long id)
    {
        return context.Users.Any(e => e.Id == id);
    }

}
