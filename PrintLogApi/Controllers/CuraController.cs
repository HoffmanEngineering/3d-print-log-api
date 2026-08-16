using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.CuraSettings;

namespace PrintLogApi.Controllers
{
    /// <summary>
    /// Operations around saving settings coming from the Cura 3D Print Log Uploader plugin.
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CuraController : ControllerBase
    {
        private readonly PrintLogContext _context;
        private readonly TelemetryClient _telemetry;

        public CuraController(PrintLogContext context, TelemetryClient telemetry)
        {
            _context = context;
            _telemetry = telemetry;
        }

        /// <summary>
        /// Returns the settings saved by cura by GUID. Since Cura does not know the user who first saved the settings,
        /// the first time the settings are retrieved by a 3D Print Log user, the settings are linked to that user.
        /// Other users cannot retrieve the settings afterwards, even if they learned the GUID.
        /// </summary>
        /// <param name="id">The GUID for the settings</param>
        /// <response code="200">An OK containing the settings send by Cura.</response>
        /// <response code="403">Returned if the user is not the owner of the settings.</response>
        [HttpGet("settings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<CuraSetting>> GetSettings(Guid id)
        {
            var userId = User.GetUserId();

            if (!userId.HasValue)
            {
                return Unauthorized();
            }

            var settings = await _context.CuraSettings.Where(c => c.Id == id).FirstOrDefaultAsync();

            if (settings == null)
            {
                return NotFound();
            }

            if (settings.UserId.HasValue)
            {
                if (settings.UserId.Value != userId.Value)
                {
                    // Return a 403 if the current user is trying to view a setting created by another user.
                    return StatusCode(StatusCodes.Status403Forbidden, "Cannot view settings linked to another user.");
                }
            }
            else 
            {
                _telemetry.TrackEvent("CuraSettingsFirstLoad");

                // Update the setting to be locked to the first user that looks at it.
                settings.UserId = userId.Value;

                _context.Entry(settings).State = EntityState.Modified;

                await _context.SaveChangesAsync();
            }

            return settings;
        }

        /// <summary>
        /// Save a JSON object containing the settings from Cura.
        /// </summary>
        /// <returns>The GUID for these saved settings that can be used to retrieve the settings.</returns>
        [HttpPost("settings")]
        [AllowAnonymous]
        public async Task<ActionResult<NewCuraSettingsDto>> SaveSettings()
        {

            _telemetry.TrackEvent("CuraSettingsSaved");

            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms);
            var encodedJsonString = ms.ToArray();  // returns base64 encoded string JSON result

            var decodedString = Encoding.UTF8.GetString(encodedJsonString);

            // Deserialize returns null for a literal "null" body, which then throws on the
            // dereference below. Null-forgiven rather than guarded to keep this change
            // annotation-only; the unvalidated webhook payload is tracked in #57.
            var settings = JsonSerializer.Deserialize<CuraSetting>(decodedString, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

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
