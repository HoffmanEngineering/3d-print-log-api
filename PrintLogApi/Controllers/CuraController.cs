using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.CuraSettings;

namespace PrintLogApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CuraController : ControllerBase
    {
        private readonly PrintLogContext _context;

        public CuraController(PrintLogContext context)
        {
            _context = context;
        }

        [HttpGet("settings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<CuraSetting>> GetSettings(Guid id)
        {
            var userId = User.GetUserId();

            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var settings = await _context.CuraSettings.Where(c => c.Id == id).FirstOrDefaultAsync();

            if (settings.UserId.HasValue)
            {
                if (settings.UserId.Value != userId.Value)
                {
                    // Return a 403 if the current user is trying to view a setting created by another user.
                    return Forbid();
                }
            }
            else 
            {
                // Update the setting to be locked to the first user that looks at it.
                settings.UserId = userId.Value;

                _context.Entry(settings).State = EntityState.Modified;

                await _context.SaveChangesAsync();
            }

            return settings;
        }

        [HttpPost("settings")]
        [AllowAnonymous]
        public async Task<ActionResult<NewCuraSettingsDto>> SaveSettings()
        {

            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            var encodedJsonString = ms.ToArray();  // returns base64 encoded string JSON result

            var decodedString = Encoding.UTF8.GetString(encodedJsonString);

            var settings = JsonSerializer.Deserialize<CuraSetting>(decodedString, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var newSettings = new CuraSetting()
            {
                CuraVersion = settings.CuraVersion,
                PluginVersion = settings.PluginVersion,
                Settings = settings.Settings,
                CreatedDate = DateTimeOffset.Now
            };

            _context.CuraSettings.Add(newSettings);
            await _context.SaveChangesAsync();

            var result = new NewCuraSettingsDto()
            {
                NewSettingId = newSettings.Id
            };

            return result;


        }
    }
}
