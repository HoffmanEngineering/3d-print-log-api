using System.Globalization;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Caching;
using PrintLogApi.Email;
using PrintLogApi.Exceptions;
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
    ISubscriptionService subscriptionService,
    IOptions<UserOnboardingOptions> onboardingOptions,
    IEmailVerificationRefresher emailVerificationRefresher,
    IAuth0Service auth0Service,
    IMemoryCache cache,
    TimeProvider clock,
    ILogger<UsersController> logger) : ControllerBase
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
    public async Task<ActionResult<UserSummaryDto>> GetUserSummary(long id)
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
    /// The address email is sent to, as last synced from the sign-in provider. Interactive
    /// sessions only: an API key must not be a way to read the account address.
    /// </summary>
    [HttpGet("me/email")]
    [Authorize(Policy = "InteractiveUserOnly")]
    public async Task<ActionResult<AccountEmailDto>> GetAccountEmail(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        return await context.Users
            .Where(u => u.Id == userId)
            .Select(u => new AccountEmailDto(u.Email, u.EmailVerified))
            .AsNoTracking()
            .SingleAsync(ct);
    }

    /// <summary>
    /// Whether the current user goes through onboarding. Interactive sessions only. Deliberately
    /// not part of the public user detail, which is anonymous.
    /// </summary>
    [HttpGet("me/onboarding")]
    [Authorize(Policy = "InteractiveUserOnly")]
    public async Task<ActionResult<OnboardingStatusDto>> GetOnboardingStatus(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var user = await context.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.CreatedDate, u.EmailVerified })
            .AsNoTracking()
            .SingleAsync(ct);

        // The token claim lags until the next sign-in, so ask Auth0 (throttled) while unverified.
        // Only for an onboarding user: nobody else is shown anything that reads this flag.
        var eligible = onboardingOptions.Value.IsEligible(user.CreatedDate);
        var emailVerified = user.EmailVerified
            || (eligible && await emailVerificationRefresher.RefreshAsync(userId.Value, ct));

        return new OnboardingStatusDto(
            eligible,
            user.CreatedDate,
            emailVerified);
    }

    /// <summary>
    /// Asks Auth0 to send the current user's verification email again. Interactive sessions only.
    /// Limited to one send per user every five minutes.
    /// </summary>
    [HttpPost("me/verification-email")]
    [Authorize(Policy = "InteractiveUserOnly")]
    public async Task<IActionResult> ResendVerificationEmail(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Unauthorized();
        }

        var throttleKey = $"verification_email_sent:{userId.Value}";
        if (cache.TryGetValue(throttleKey, out DateTimeOffset expiresAt))
        {
            // Rounded up, never down: Retry-After is whole seconds, and truncating sends a
            // client that honors the header back before the window has closed.
            var seconds = Math.Max(1, (int)Math.Ceiling((expiresAt - clock.GetUtcNow()).TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return CodedProblem(StatusCodes.Status429TooManyRequests, "A verification email was sent recently. Try again later.", "rate_limited");
        }

        // Forced, so a GET me/onboarding a moment ago (which set the refresher's throttle) cannot
        // hide a verification that just happened. The rate limit above bounds the Auth0 calls.
        if (await emailVerificationRefresher.RefreshAsync(userId.Value, ct, force: true))
        {
            return CodedProblem(StatusCodes.Status409Conflict, "The email address is already verified.", "already_verified");
        }

        var oauthUserId = await context.Users
            .Where(u => u.Id == userId)
            .Select(u => u.OAuthUserId)
            .SingleOrDefaultAsync(ct);

        // Without an Auth0 id there is nothing to resend to. Leave the throttle unset.
        if (string.IsNullOrEmpty(oauthUserId))
        {
            return CodedProblem(StatusCodes.Status502BadGateway, "The verification email could not be sent right now.", "auth0_unavailable");
        }

        try
        {
            await auth0Service.SendVerificationEmail(oauthUserId, ct);
        }
        // HttpClient's own timeout is a TaskCanceledException while ct is still live.
        catch (Exception ex) when (ex is Auth0ApiException or HttpRequestException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Logged so a missing update:users grant on the Management API client shows up.
            logger.LogWarning(ex, "Resending the verification email failed for user {UserId}", userId.Value);
            return CodedProblem(StatusCodes.Status502BadGateway, "The verification email could not be sent right now.", "auth0_unavailable");
        }

        // Deliberately IMemoryCache, not HybridCache: a throttle flag, not a computed value.
        // The value is the expiry instant so a later call can report the time remaining.
        var window = TimeSpan.FromMinutes(5);
        cache.Set(throttleKey, clock.GetUtcNow().Add(window), new MemoryCacheEntryOptions()
            .SetSize(CacheBudget.SmallEntryBytes)
            .SetAbsoluteExpiration(window));

        telemetry.TrackEvent("VerificationEmail_Sent", new Dictionary<string, string>
        {
            { "UserId", userId.Value.ToString(CultureInfo.InvariantCulture) },
        });

        return NoContent();
    }

    private ObjectResult CodedProblem(int status, string detail, string code)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, detail: detail);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
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
