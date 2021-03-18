using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Models.DTOs.UserApiKeys;
using PrintLogApi.Services;
using PrintLogApi.Extensions;
using PrintLogApi.Exceptions;

namespace PrintLogApi.Controllers
{
    [Route("api/UserApiKeys")]
    [ApiController]
    [Authorize]
    public class UserApiKeysController : ControllerBase
    {
        private readonly IUserApiKeyService _userApiKeyService;
        private readonly IMapper _mapper;
        private readonly TelemetryClient _telemetry;



        public UserApiKeysController(IUserApiKeyService userApiKeyService, IMapper mapper, TelemetryClient telemetry)
        {
            _userApiKeyService = userApiKeyService;
            _mapper = mapper;
            _telemetry = telemetry;

        }

        [HttpGet]
        public async Task<ActionResult<List<UserApiKeyDto>>> GetApiKeySummaryForUser()
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var apiKeys = await _userApiKeyService.GetApiKeySummaryForUser(userId.Value);

            return apiKeys;
        }

        [HttpPost]
        public async Task<ActionResult<NewUserApiKeyDto>> GenerateNewApiKey([FromBody] AddNewApiKeyDto request)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var newKey = await _userApiKeyService.GenerateNewApiKey(userId.Value, request.Description);
    

            return newKey;
        }

        [HttpDelete("{apiKey}")]
        public async Task<ActionResult<NewUserApiKeyDto>> DeleteApiKey([FromRoute] Guid apiKey)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                await _userApiKeyService.DeactivateApiKey(apiKey, userId.Value);
                return NoContent();
            } catch (DoesNotExistException)
            {
                return NotFound("Active API Key Not Found");
            } catch (UserCannotAccessApiKeyException)
            {
                return Forbid("User does not have access to specified API Key");
            }
            
            
        }
    }
}
