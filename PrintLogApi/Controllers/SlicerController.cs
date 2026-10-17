using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrintLog.SlicerSettings;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.CuraSettings;
using PrintLogApi.Models.DTOs.Slicer;
using PrintLogApi.OpenApi;
using PrintLogApi.Services.SlicerSettings;

namespace PrintLogApi.Controllers;

/// <summary>
/// Uploads from slicer plugins that send the G-code itself and let the server parse it: the
/// OrcaSlicer plugin (3d-print-log-orca-plugin).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[AllowAnonymous]
public class SlicerController(PrintLogContext context, TelemetryClient telemetry) : ControllerBase
{
    /// <summary>The whole request, multipart framing included: 8 MiB.</summary>
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    /// <summary>The <c>metadata</c> part: 1 MiB.</summary>
    public const int MaxMetadataBytes = 1024 * 1024;

    /// <summary>The inflated G-code: two 2,000,000-byte windows and the newline between them.</summary>
    public const int MaxGcodeChars = 4_000_001;

    /// <summary>The parser renders at most this many models.</summary>
    public const int MaxModels = 1000;

    /// <summary>A process, a printer and one filament per slot, each with its inherits chain.</summary>
    public const int MaxUserPresets = 64;

    private const int MaxPresetNameLength = 256;

    /// <summary>What the parser reports when the header named no version.</summary>
    private const string UnknownVersion = "Unknown";

    private static readonly HashSet<string> PresetKinds = new(StringComparer.Ordinal) { "process", "filament", "machine" };

    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Saves the settings of a G-code the OrcaSlicer plugin uploaded, for the browser to claim.
    /// </summary>
    /// <remarks>
    /// Anonymous, like `POST /api/Cura/settings`: the response's `newSettingId` goes into the
    /// new-print page's URL, and the first signed-in user to load it through
    /// `GET /api/Cura/settings` owns it.
    ///
    /// The body is `multipart/form-data` with two parts. `gcode` is the G-code as gzip-compressed
    /// UTF-8 text: the whole file when it is at most 4,000,000 bytes, otherwise its first and last
    /// 2,000,000 bytes joined by a newline. `metadata` is a JSON object: `pluginVersion` (required),
    /// `template` (`default` or `full`, required), `slicerVersion`, `outputName`, `host`,
    /// `models` (at most 1,000 of `{name, minX, maxX, minY, maxY, maxZ}`, every field required)
    /// and `userPresets` (at most 64 of `{kind, name, json}`, `kind` one of `process`, `filament`,
    /// `machine`, `json` the preset file's text; one that is not a JSON object is ignored).
    ///
    /// Host and credential values (`print_host`, `printhost_apikey`, ...) are removed from the
    /// text and from every preset before anything is parsed, and the text itself is not stored.
    /// Limited per client address: 10 a minute and 200 a day.
    /// </remarks>
    /// <response code="200">Stored. The id to pass to the new-print page.</response>
    /// <response code="400">A part is missing or sent twice, the gcode is not gzip or is empty, or the metadata is not valid.</response>
    /// <response code="413">The request is over 8 MiB, the metadata over 1 MiB, or the G-code over 4,000,001 characters.</response>
    /// <response code="415">The body is not multipart/form-data.</response>
    /// <response code="429">Too many uploads from this address. Retry after the `Retry-After` header's seconds.</response>
    [HttpPost("uploads")]
    [EnableRateLimiting(SlicerUploadRateLimiting.Policy)]
    // Kestrel's limit, enforced as the body is read. The reader counts the body too, because the
    // test server has no such feature and a chunked body has no Content-Length to check.
    [RequestSizeLimit(MaxRequestBytes)]
    [OpenApiRequestBody(typeof(SlicerUploadForm), ContentType = "multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    // No parameters, not even a CancellationToken: an action that binds anything makes MVC build
    // its value providers, and the form one reads the whole multipart body before the action runs,
    // past every cap below. SlicerControllerTests fails on every upload if one is added.
    public async Task<ActionResult<NewCuraSettingsDto>> PostUpload()
    {
        var cancellationToken = HttpContext.RequestAborted;
        try
        {
            var parts = await SlicerUploadReader.ReadAsync(Request, MaxRequestBytes, MaxMetadataBytes, cancellationToken);

            // The metadata first: a request that is going to be refused should not cost a gunzip.
            var metadata = ReadMetadata(parts.Metadata);
            var gcode = SlicerUploadReader.Gunzip(parts.Gcode, MaxGcodeChars);
            if (gcode.Length == 0)
            {
                throw Invalid("The gcode part holds no text.");
            }

            return await Save(gcode, metadata, cancellationToken);
        }
        catch (SlicerUploadRejectedException e)
        {
            return Problem(statusCode: e.StatusCode, title: e.Title, detail: e.Message);
        }
    }

    private async Task<NewCuraSettingsDto> Save(string gcode, Metadata metadata, CancellationToken ct)
    {
        SlicerGcodeResult result;
        try
        {
            // Scrubbed before the parser sees anything. No debug log: its messages can quote the text.
            result = SlicerGcodeParser.Parse(
                SlicerSettingsScrubber.ScrubText(gcode),
                new SlicerGcodeOptions(metadata.Template, metadata.Models, metadata.UserPresets) { OutputName = metadata.OutputName });
        }
        catch (ArgumentException e)
        {
            // Only the template is checked this way, and ReadMetadata has checked it already.
            throw Invalid(e.Message);
        }

        // The header's version, else the one the plugin read from the slicer.
        var version = result.Version != UnknownVersion || metadata.SlicerVersion is null ? result.Version : metadata.SlicerVersion;

        using var settings = JsonDocument.Parse(result.SettingsJson);
        var record = new CuraSetting
        {
            // Stored raw. SlicerNames.Normalize runs when a print is created from this setting.
            Slicer = Truncate(result.Slicer, 50),
            CuraVersion = Truncate(version, 100),
            PluginVersion = Truncate($"orca-plugin/{metadata.PluginVersion}", 100),
            Settings = settings.RootElement.Clone(),
            CreatedDate = DateTimeOffset.UtcNow,
        };
        context.CuraSettings.Add(record);
        await context.SaveChangesAsync(ct);

        // Nothing from the text, the file name or the presets: counts and fixed vocabularies only.
        telemetry.TrackEvent("SlicerUploadSaved", new Dictionary<string, string>
        {
            ["Slicer"] = record.Slicer ?? string.Empty,
            ["Recognized"] = result.IsRecognized.ToString(),
            ["Template"] = metadata.Template.ToLowerInvariant(),
            ["Destination"] = metadata.IsExport ? "file" : "printHost",
            ["Models"] = metadata.Models.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["UserPresets"] = metadata.UserPresets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        return new NewCuraSettingsDto { NewSettingId = record.Id };
    }

    /// <summary>The metadata, validated, with each preset scrubbed (and dropped when it is not a JSON object).</summary>
    private static Metadata ReadMetadata(byte[] json)
    {
        SlicerUploadMetadataDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SlicerUploadMetadataDto>(json, MetadataJson);
        }
        catch (JsonException)
        {
            throw Invalid("metadata is not a JSON object with the documented fields and types.");
        }

        if (dto is null)
        {
            throw Invalid("metadata is not a JSON object.");
        }

        if (string.IsNullOrWhiteSpace(dto.PluginVersion))
        {
            throw Invalid("metadata.pluginVersion is required.");
        }

        if (dto.Template?.Trim() is not { } template
            || (!template.Equals("default", StringComparison.OrdinalIgnoreCase) && !template.Equals("full", StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid("metadata.template must be \"default\" or \"full\".");
        }

        var models = dto.Models ?? [];
        if (models.Count > MaxModels)
        {
            throw Invalid($"metadata.models has more than {MaxModels} entries.");
        }

        var bounds = new List<ModelBounds>(models.Count);
        for (var i = 0; i < models.Count; i++)
        {
            if (models[i] is not { Name: { } name, MinX: { } minX, MaxX: { } maxX, MinY: { } minY, MaxY: { } maxY, MaxZ: { } maxZ })
            {
                throw Invalid($"metadata.models[{i}] needs name, minX, maxX, minY, maxY and maxZ.");
            }

            bounds.Add(new ModelBounds(name, minX, maxX, minY, maxY, maxZ));
        }

        var presets = dto.UserPresets ?? [];
        if (presets.Count > MaxUserPresets)
        {
            throw Invalid($"metadata.userPresets has more than {MaxUserPresets} entries.");
        }

        var files = new List<UserPresetFile>(presets.Count);
        for (var i = 0; i < presets.Count; i++)
        {
            if (presets[i] is not { Kind: { } kind, Name: { } name, Json: { } text }
                || !PresetKinds.Contains(kind)
                || string.IsNullOrWhiteSpace(name)
                || name.Length > MaxPresetNameLength)
            {
                throw Invalid($"metadata.userPresets[{i}] needs kind (process, filament or machine), a name of at most {MaxPresetNameLength} characters, and json.");
            }

            // A preset the plugin read but that is not a JSON object is one the parser would
            // ignore anyway; dropping it keeps a corrupt preset file from costing the upload.
            if (SlicerSettingsScrubber.ScrubJson(text) is { } scrubbed)
            {
                files.Add(new UserPresetFile(kind, name, scrubbed));
            }
        }

        return new Metadata(
            dto.PluginVersion.Trim(),
            string.IsNullOrWhiteSpace(dto.SlicerVersion) ? null : SlicerSettingsNote.OneLine(dto.SlicerVersion),
            string.IsNullOrEmpty(dto.OutputName) ? null : dto.OutputName,
            IsExport: string.Equals(dto.Host, "File", StringComparison.Ordinal),
            template,
            bounds,
            files);
    }

    private sealed record Metadata(
        string PluginVersion,
        string? SlicerVersion,
        string? OutputName,
        bool IsExport,
        string Template,
        List<ModelBounds> Models,
        List<UserPresetFile> UserPresets);

    private static SlicerUploadRejectedException Invalid(string detail) =>
        new(StatusCodes.Status400BadRequest, "Invalid upload", detail);

    private static string? Truncate(string? value, int length) =>
        value is { } v && v.Length > length ? v[..length] : value;
}
