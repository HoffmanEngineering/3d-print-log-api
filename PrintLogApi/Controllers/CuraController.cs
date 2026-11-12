using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.CuraSettings;

namespace PrintLogApi.Controllers;

/// <summary>
/// Operations around saving settings coming from the Cura 3D Print Log Uploader plugin.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class CuraController(PrintLogContext context, TelemetryClient telemetry) : ControllerBase
{
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

        var settings = await context.CuraSettings.Where(c => c.Id == id).FirstOrDefaultAsync();

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
            telemetry.TrackEvent("CuraSettingsFirstLoad");

            // Update the setting to be locked to the first user that looks at it.
            settings.UserId = userId.Value;

            context.Entry(settings).State = EntityState.Modified;

            await context.SaveChangesAsync();
        }

        return settings;
    }

    /// <summary>
    /// Largest accepted body: the same 4 MiB as <c>POST api/Prints/{id}/slicer-settings</c>. The
    /// Slic3r uploader reads at most a 2,000,000-character window of the G-code, so its thumbnail
    /// cannot exceed about 2 MB, and the Cura plugin's 300x300 snapshot is far smaller.
    /// </summary>
    private const int MaxSettingsRequestBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Save a JSON object containing the settings from Cura.
    /// </summary>
    /// <remarks>
    /// The body is read by hand rather than bound, because the Cura plugin posts it without a
    /// Content-Type header, which model binding would refuse with a 415.
    /// </remarks>
    /// <returns>The GUID for these saved settings that can be used to retrieve the settings.</returns>
    /// <response code="200">The id of the saved settings.</response>
    /// <response code="400">The body is not a JSON object, or a version or slicer name is too long.</response>
    /// <response code="413">The body is over 4 MiB.</response>
    [HttpPost("settings")]
    [AllowAnonymous]
    [RequestSizeLimit(MaxSettingsRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<NewCuraSettingsDto>> SaveSettings()
    {

        telemetry.TrackEvent("CuraSettingsSaved");

        // Deserialized straight from the request stream: the stored settings text is the raw JSON
        // of the "settings" object either way, so nothing about what is stored changes.
        CuraSetting? settings;
        try
        {
            settings = await JsonSerializer.DeserializeAsync<CuraSetting>(Request.Body, SettingsJsonOptions, HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            settings = null;
        }

        if (settings is null)
        {
            ModelState.AddModelError("body", "The body must be a JSON object.");
            return ValidationProblem(ModelState);
        }

        AddLengthError(nameof(CuraSetting.CuraVersion), settings.CuraVersion, CuraSetting.MaxVersionLength);
        AddLengthError(nameof(CuraSetting.PluginVersion), settings.PluginVersion, CuraSetting.MaxVersionLength);
        AddLengthError(nameof(CuraSetting.Slicer), settings.Slicer, CuraSetting.MaxSlicerLength);
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var newSettings = new CuraSetting()
        {
            CuraVersion = settings.CuraVersion,
            PluginVersion = settings.PluginVersion,
            // Stored raw. SlicerNames.Normalize runs when a print is created from this setting.
            Slicer = settings.Slicer,
            Settings = settings.Settings,
            CreatedDate = DateTimeOffset.Now
        };

        context.CuraSettings.Add(newSettings);
        await context.SaveChangesAsync();

        var result = new NewCuraSettingsDto()
        {
            NewSettingId = newSettings.Id
        };

        return result;


    }

    private void AddLengthError(string field, string? value, int maxLength)
    {
        if (value is not null && value.Length > maxLength)
        {
            ModelState.AddModelError(JsonNamingPolicy.CamelCase.ConvertName(field), $"The field {field} must be a string with a maximum length of {maxLength}.");
        }
    }
}
