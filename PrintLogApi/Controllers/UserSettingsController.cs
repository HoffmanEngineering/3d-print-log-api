using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserSetting;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Manage the currently authenticated user's list of settings. User Settings are used throughout the application to
    /// store preferences, last selected printers, etc.
    /// </summary>
    [Route("api/Users/me/user-settings")]
    [ApiController]
    [Authorize]
    public class UserSettingsController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly IMapper _mapper;
        private readonly Services.ICacheVersionService _cacheVersionService;

        public UserSettingsController(
            PrintLogContext context,
            IMapper mapper,
            Services.ICacheVersionService cacheVersionService)
        {
            _context = context;
            _mapper = mapper;
            _cacheVersionService = cacheVersionService;
        }

        /// <summary>
        /// Returns the list of the current user's UserSettings.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserSettingDto>>> GetCurrentUsersSettings()
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var settings = await _context.UserSettings
                .Where(u => u.UserId == userId)
                .AsNoTracking()
                .ProjectTo<UserSettingDto>(_mapper.ConfigurationProvider)
                .ToListAsync();

            return settings;
        }

        /// <summary>
        /// Update an existing user setting.
        /// </summary>
        /// <param name="updateSettingDto">The details of the user setting to update.</param>
        /// <returns></returns>
        [HttpPut]
        public async Task<ActionResult<UserSettingDto>> UpdateUserSetting([FromBody] UpdateUserSettingDto updateSettingDto)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var existingSetting = await _context.UserSettings
                .Where(setting => setting.Id == updateSettingDto.Id && setting.UserId == userId)
                .SingleOrDefaultAsync();

            if (existingSetting == null)
            {
                return NotFound();
            }

            existingSetting = _mapper.Map(updateSettingDto, existingSetting);

            _context.Entry(existingSetting).State = EntityState.Modified;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!UserSettingExists(updateSettingDto.Id))
                {
                    return NotFound();
                }
                else
                {
                    throw;
                }
            }

            // Analytics costs are derived from settings (currency, default material price,
            // electricity rate and wattage), so a settings change makes any cached aggregate
            // wrong. Without this the user edits their kWh rate and the cost tile keeps the
            // old figure for up to the cache TTL.
            _cacheVersionService.InvalidateUserCache(userId.Value);

            return _mapper.Map<UserSettingDto>(existingSetting);
        }

        /// <summary>
        /// Save a new user setting.
        /// </summary>
        /// <param name="newSettingDto"></param>
        /// <returns></returns>
        [HttpPost]
        public async Task<ActionResult<UserSettingDto>> CreateUserSetting([FromBody] AddUserSettingDto newSettingDto)
        {
            var userId = User.GetUserId();
            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var existingSetting = await _context.UserSettings
                .Where(setting => setting.UserSettingTypeId == newSettingDto.UserSettingTypeId && setting.UserId == userId)
                .SingleOrDefaultAsync();

            if (existingSetting != null)
            {
                return BadRequest("UserSetting for this SettingTypeId already exists.");
            }

            var newSetting = _mapper.Map<UserSetting>(newSettingDto);

            newSetting.UserId = userId.Value;
            newSetting.CreatedById = userId.Value;
            newSetting.UpdatedById = userId.Value;


            _context.UserSettings.Add(newSetting);
            await _context.SaveChangesAsync();

            // As with the update path: a newly-set price or rate changes every cached cost.
            _cacheVersionService.InvalidateUserCache(userId.Value);

            return _mapper.Map<UserSettingDto>(newSetting);
        }

        private bool UserSettingExists(long id)
        {
            return _context.UserSettings.Any(e => e.Id == id);
        }
    }
}
