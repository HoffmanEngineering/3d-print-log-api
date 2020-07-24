using AutoMapper;
using AutoMapper.QueryableExtensions;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.User;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly IAuthorizationService _authorizationService;
        private readonly TelemetryClient _telemetry;

        private readonly string profileImageContainerName = "userprofile";
        private readonly BlobContainerClient userProfileImageContainer;

        public UsersController(PrintLogContext context, IMapper mapper, IConfiguration config, IAuthorizationService authorizationService, TelemetryClient telemetry)
        {
            _context = context;
            _mapper = mapper;
            _authorizationService = authorizationService;
            _telemetry = telemetry;

            BlobServiceClient blobServiceClient = new BlobServiceClient(config["AZURE_STORAGE_CONNECTION_STRING"]);
            userProfileImageContainer = blobServiceClient.GetBlobContainerClient(profileImageContainerName);
        }

        [HttpGet("{id}/summary")]
        [AllowAnonymous]
        public async Task<ActionResult<UserSummaryDto>> GetCurrentUserDetails(long id)
        {
            UserSummaryDto user = await _context.Users
                .Where(u => u.Id == id)
                .ProjectTo<UserSummaryDto>(_mapper.ConfigurationProvider)
                .AsNoTracking()
                .SingleAsync();

            return user;
        }

        [HttpGet("me")]
        public async Task<ActionResult<UserDetailDto>> GetCurrentUserDetails()
        {
            long userId = long.Parse(User.FindFirst(ClaimTypes.NameIdentifier).Value);

            UserDetailDto user = await _context.Users
                .Where(u => u.Id == userId)
                .ProjectTo<UserDetailDto>(_mapper.ConfigurationProvider)
                .AsNoTracking()
                .SingleAsync();

            return user;
        }

        [HttpGet("{id}")]
        [AllowAnonymous]
        public async Task<ActionResult<UserDetailDto>> GetUserDetails(long id)
        {
            User user = await _context.Users
            .Where(u => u.Id == id)
            .AsNoTracking()
            .SingleAsync();

            if (!await CanViewUserProfile(user))
            {
                return Forbid();
            }

            return this._mapper.Map<UserDetailDto>(user);
        }

        [HttpPut("me")]
        public async Task<ActionResult<UserDetailDto>> UpdateCurrentUserDetails(UpdateUserDetailDto updatedUser)
        {
            long userId = long.Parse(User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var existingUser = await _context.Users
                .Where(u => u.Id == userId)
                .SingleAsync();

            if (existingUser == null)
            {
                return NotFound();
            }


            existingUser = _mapper.Map(updatedUser, existingUser);

            _context.Entry(existingUser).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!UserExists(userId))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            return _mapper.Map<UserDetailDto>(existingUser);
        }

        [HttpPost("me/profile-image")]
        public async Task<ActionResult<UserUrlDto>> PostProfileImage([FromForm] IFormFile image)
        {
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var user = await _context.Users.FindAsync(userId);

            if (user == null)
            {
                return NotFound();
            }

            Guid fileId = Guid.NewGuid();
            string fileName = fileId + Path.GetExtension(image.FileName);

            BlobClient blobClient = userProfileImageContainer.GetBlobClient(fileName);

            using (Stream uploadFileStream = image.OpenReadStream())
            {
                var info = await blobClient.UploadAsync(uploadFileStream);

            };

            
            var file = new Models.File()
            {
                Size = image.Length,
                Path = $"{this.profileImageContainerName}/{fileName}",
                Id = fileId,
                CreatedById = userId,
                UpdatedById = userId,
            };
            _context.Files.Add(file);

            user.ProfilePicture = blobClient.Uri.AbsoluteUri;

            await _context.SaveChangesAsync();
            _telemetry.TrackEvent("UserProfilePictureUploaded");

            return new UserUrlDto() { Url = blobClient.Uri.AbsoluteUri };
        }

        [HttpPost("me/cover-image")]
        public async Task<ActionResult<UserUrlDto>> PostCoverImage([FromForm] IFormFile image)
        {
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var user = await _context.Users.FindAsync(userId);

            if (user == null)
            {
                return NotFound();
            }

            Guid fileId = Guid.NewGuid();
            string fileName = fileId + Path.GetExtension(image.FileName);

            BlobClient blobClient = userProfileImageContainer.GetBlobClient(fileName);

            using (Stream uploadFileStream = image.OpenReadStream())
            {
                var info = await blobClient.UploadAsync(uploadFileStream);
            };


            var file = new Models.File()
            {
                Size = image.Length,
                Path = $"{this.profileImageContainerName}/{fileName}",
                Id = fileId,
                CreatedById = userId,
                UpdatedById = userId,
            };
            _context.Files.Add(file);

            user.CoverPicture = blobClient.Uri.AbsoluteUri;

            await _context.SaveChangesAsync();
            _telemetry.TrackEvent("UserCoverPictureUploaded");

            return new UserUrlDto() { Url = blobClient.Uri.AbsoluteUri };
        }

        /// <summary>
        /// Remove the current user's cover-image.
        /// </summary>
        /// <returns></returns>
        [HttpDelete("me/cover-image")]
        public async Task<ActionResult<UserUrlDto>> RemoveCoverImage()
        {
            long userId = long.Parse(this.User.FindFirst(ClaimTypes.NameIdentifier).Value);

            var user = await _context.Users.FindAsync(userId);

            if (user == null)
            {
                return NotFound();
            }

            user.CoverPicture = null;

            await _context.SaveChangesAsync();

            return Ok();
        }

        /// <summary>
        /// Helper method to  check if the current user can view print
        /// </summary>
        /// <param name="print"></param>
        /// <returns></returns>
        private async Task<bool> CanViewUserProfile(User profileToView)
        {
            var authorizationResult = await _authorizationService
                            .AuthorizeAsync(User, profileToView, "ViewUserProfile");

            return authorizationResult.Succeeded;

        }

        private bool UserExists(long id)
        {
            return _context.Users.Any(e => e.Id == id);
        }

    }
}
