using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Spoolman;
using PrintLogApi.Extensions;
using PrintLogApi.Services.Spoolman;

namespace PrintLogApi.Controllers;

/// <summary>
/// The read-only Spoolman v1 API (#177), so Moonraker's <c>[spoolman]</c> component, and through
/// it Mainsail, Fluidd and KlipperScreen, can use 3D Print Log as their spool server.
///
/// <para>Everything is scoped to the connection's user. Bodies are written by
/// <see cref="SpoolmanJsonContext"/>, never by MVC's formatter, so the app's JSON options cannot
/// change a shape clients parse; errors are Spoolman's <c>{"message"}</c>. Kept out of the public
/// OpenAPI document: this is Spoolman's contract, not ours. It has its own rate-limit policy,
/// partitioned by connection, so a user's own REST traffic cannot starve their printers.</para>
///
/// <para>Writes (<c>POST</c>/<c>PATCH</c>/<c>DELETE</c>, <c>/measure</c>, settings) answer 400 until
/// #172. <c>PUT /spool/{id}/use</c> is recorded against the printer's open notifier print (#171).</para>
/// </summary>
[Route("spoolman/api/v1")]
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize(Policy = SpoolmanAuthentication.Policy)]
[EnableRateLimiting(RateLimitPolicy)]
public class SpoolmanController(
    ISpoolmanInventoryService inventory,
    SpoolmanSocketHost sockets,
    ISpoolmanUsageRecorder usage,
    PrintLogContext context,
    TelemetryClient telemetry) : ControllerBase
{
    public const string RateLimitPolicy = "spoolman";

    /// <summary>
    /// Reported as the Spoolman version. Happy Hare requires at least 0.18.1 and Fluidd 0.16.0 for
    /// its currency call; this is the release whose models these shapes follow.
    /// </summary>
    public const string SpoolmanVersion = "0.27.0";

    public const string NotSupportedMessage = "Not supported yet by 3D Print Log";

    /// <summary>Spoolman's settings and their defaults (spoolman/settings.py), all JSON-encoded strings.</summary>
    private static readonly (string Key, string Type, string Default)[] Settings =
    [
        ("currency", "string", "\"USD\""),
        ("round_prices", "boolean", "false"),
        ("print_presets", "array", "[]"),
        ("label_designs", "array", "[]"),
        ("extra_fields_vendor", "array", "[]"),
        ("extra_fields_filament", "array", "[]"),
        ("extra_fields_spool", "array", "[]"),
        ("base_url", "string", "\"\""),
        ("locations", "array", "[]"),
        ("locations_spoolorders", "object", "{}"),
        ("dashboard_groups", "object", "{}"),
        ("dashboard_spoolorders", "object", "{}"),
    ];

    private static readonly string[] FieldEntities = ["vendor", "filament", "spool"];

    private const int CurrencySettingTypeId = 5; // Currency_Name

    [HttpGet("info")]
    public IActionResult Info() => Json(
        new SpoolmanInfo(SpoolmanVersion, DebugMode: false, AutomaticBackups: false, DataDir: "", LogsDir: "", BackupsDir: "",
            DbType: "sqlite", ExternalDbName: "SpoolmanDB"),
        SpoolmanJsonContext.Wire.SpoolmanInfo);

    [HttpGet("health")]
    public IActionResult Health() => Json(new SpoolmanHealth("healthy"), SpoolmanJsonContext.Wire.SpoolmanHealth);

    /// <summary>
    /// Spools. Called unpaginated by every client, so it serves the cached inventory. Archived
    /// spools are hidden unless <c>allow_archived=true</c>; the total before paging is in
    /// <c>x-total-count</c>.
    /// </summary>
    [HttpGet("spool")]
    public async Task<IActionResult> FindSpools()
    {
        // Moonraker's websocket (#178) shares this path with the REST list.
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            await sockets.RunAsync(HttpContext);
            return new EmptyResult();
        }

        var all = await InventoryAsync();
        var q = Request.Query;
        try
        {
            var filamentIds = SpoolmanQuery.ParseIds(Param("filament.id", "filament_id"), "filament.id");
            var vendorIds = SpoolmanQuery.ParseIds(Param("filament.vendor.id", "vendor_id"), "filament.vendor.id");
            var allowArchived = Bool("allow_archived");

            var matches = all.Spools.Where(s =>
                (allowArchived || !s.Archived)
                && SpoolmanQuery.MatchesTerms(s.Filament.Name, Param("filament.name", "filament_name"))
                && SpoolmanQuery.MatchesTerms(s.Filament.Material, Param("filament.material", "filament_material"))
                && SpoolmanQuery.MatchesTerms(s.Filament.Vendor?.Name, Param("filament.vendor.name", "vendor_name"))
                && SpoolmanQuery.MatchesTerms(s.Location, Param("location"))
                && SpoolmanQuery.MatchesTerms(s.LotNr, Param("lot_nr"))
                && SpoolmanQuery.MatchesTerms(s.Filament.MultiColorDirection, Param("filament.multi_color_direction"))
                && (filamentIds is null || filamentIds.Contains(s.Filament.Id))
                && (vendorIds is null || (s.Filament.Vendor is { } v && vendorIds.Contains(v.Id))));

            var sorted = SpoolmanQuery.Sort(matches, q["sort"].FirstOrDefault(), SpoolmanQuery.SpoolField, s => s.Id);
            return Page(sorted, SpoolmanJsonContext.Wire.IReadOnlyListSpoolmanSpool);
        }
        catch (SpoolmanQueryException ex)
        {
            return Message(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    /// <summary>
    /// One spool. Moonraker calls this on connect and whenever the active spool is set, and a 404
    /// <b>clears its active spool</b>, so it is 404 only for a number the user really has no spool
    /// under. Archived spools are returned.
    /// </summary>
    [HttpGet("spool/{id:int}")]
    public async Task<IActionResult> GetSpool(int id)
    {
        // Spoolman serves a socket for one spool here too (#178).
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            await sockets.RunAsync(HttpContext);
            return new EmptyResult();
        }

        return (await InventoryAsync()).Spools.FirstOrDefault(s => s.Id == id) is { } spool
            ? Json(spool, SpoolmanJsonContext.Wire.SpoolmanSpool)
            : Message(StatusCodes.Status404NotFound, $"No spool with ID {id} found.");
    }

    /// <summary>
    /// Spoolman's root websocket, for changes to anything (#178): spoolman2slicer opens it with the
    /// credential. Fluidd opens it straight from the browser, not through Moonraker and without the
    /// printer's credential, so it is refused (401) and Fluidd shows a "live updates" warning. A
    /// plain GET here is a 404, as in Spoolman.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Root()
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            await sockets.RunAsync(HttpContext);
            return new EmptyResult();
        }

        return Message(StatusCodes.Status404NotFound, "Not found.");
    }

    /// <summary>
    /// Moonraker's usage report. Validated as Spoolman does (exactly one of <c>use_length</c> and
    /// <c>use_weight</c>) and answered with the spool. Added to the spool's running total on the
    /// connection's printer's open notifier print (#171), which the notifier's finish turns into the
    /// print's usage; with no such print, or with a live printlog-bridge logging the printer, it is
    /// not stored (<see cref="ISpoolmanUsageRecorder"/>). Answered the same either way, because
    /// Moonraker cannot act on the difference. Counted, per user in Application Insights and by
    /// outcome in Prometheus.
    /// </summary>
    [HttpPut("spool/{id:int}/use")]
    public async Task<IActionResult> Use(int id)
    {
        SpoolmanUseRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(Request.Body, SpoolmanJsonContext.Wire.SpoolmanUseRequest, HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return Message(StatusCodes.Status400BadRequest, "The request body is not valid JSON.");
        }

        if (body?.UseWeight is not null && body.UseLength is not null)
        {
            return Message(StatusCodes.Status400BadRequest, "Only specify either use_weight or use_length.");
        }

        if (body?.UseWeight is null && body?.UseLength is null)
        {
            return Message(StatusCodes.Status400BadRequest, "Either use_weight or use_length must be specified.");
        }

        if (!double.IsFinite(body.UseWeight ?? body.UseLength!.Value))
        {
            return Message(StatusCodes.Status400BadRequest, "use_weight and use_length must be numbers.");
        }

        if ((await InventoryAsync()).Spools.FirstOrDefault(s => s.Id == id) is not { } spool)
        {
            return Message(StatusCodes.Status404NotFound, $"No spool with ID {id} found.");
        }

        var millimetres = body.UseLength
            ?? SpoolmanInventoryService.LengthMm(body.UseWeight!.Value, spool.Filament.Density, spool.Filament.Diameter);
        var unit = body.UseLength is not null ? "length" : "weight";

        SpoolmanMetrics.UseCalls.WithLabels(unit).Inc();
        if (millimetres > 0)
        {
            SpoolmanMetrics.UseMillimetres.Inc(millimetres);
        }
        // Pre-aggregated per minute by the SDK: one metric per user, count = calls, sum = mm.
        var userId = UserId();
        telemetry.GetMetric("Spoolman_UseMm", "userId").TrackValue(millimetres, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The wire id is the spool number; the inventory carries no filament id. Null only if the
        // filament was deleted after the cached inventory was read, and then there is nothing to record.
        var filamentId = await context.Filaments.AsNoTracking()
            .Where(f => f.CreatedById == userId && f.SpoolNumber == id)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        // A finite use_weight near double.MaxValue converts to an infinite length, which SQL Server's
        // float column cannot hold. Nothing real reports that; answer it, but store nothing.
        if (filamentId is { } filament && double.IsFinite(millimetres))
        {
            var printerId = long.TryParse(User.FindFirst(SpoolmanAuthentication.PrinterIdClaim)?.Value, out var claimed) ? claimed : (long?)null;
            var outcome = await usage.RecordAsync(userId, printerId, filament, millimetres, HttpContext.RequestAborted);
            SpoolmanMetrics.UseRecorded.WithLabels(SpoolmanMetrics.OutcomeLabel(outcome)).Inc();
        }

        return Json(spool, SpoolmanJsonContext.Wire.SpoolmanSpool);
    }

    [HttpGet("filament")]
    public async Task<IActionResult> FindFilaments()
    {
        var all = await InventoryAsync();
        try
        {
            var vendorIds = SpoolmanQuery.ParseIds(Param("vendor.id", "vendor_id"), "vendor.id");
            var matches = all.Spools.Select(s => s.Filament).Where(f =>
                SpoolmanQuery.MatchesTerms(f.Name, Param("name"))
                && SpoolmanQuery.MatchesTerms(f.Material, Param("material"))
                && SpoolmanQuery.MatchesTerms(f.Vendor?.Name, Param("vendor.name", "vendor_name"))
                && SpoolmanQuery.MatchesTerms(f.ArticleNumber, Param("article_number"))
                && SpoolmanQuery.MatchesTerms(f.ExternalId, Param("external_id"))
                && (vendorIds is null || (f.Vendor is { } v && vendorIds.Contains(v.Id))));

            var sorted = SpoolmanQuery.Sort(matches, Request.Query["sort"].FirstOrDefault(), SpoolmanQuery.FilamentField, f => f.Id);
            return Page(sorted, SpoolmanJsonContext.Wire.IReadOnlyListSpoolmanFilament);
        }
        catch (SpoolmanQueryException ex)
        {
            return Message(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    [HttpGet("filament/{id:int}")]
    public async Task<IActionResult> GetFilament(int id) =>
        (await InventoryAsync()).Spools.FirstOrDefault(s => s.Filament.Id == id) is { } spool
            ? Json(spool.Filament, SpoolmanJsonContext.Wire.SpoolmanFilament)
            : Message(StatusCodes.Status404NotFound, $"No filament with ID {id} found.");

    [HttpGet("vendor")]
    public async Task<IActionResult> FindVendors()
    {
        var all = await InventoryAsync();
        try
        {
            var matches = all.Vendors.Where(v =>
                SpoolmanQuery.MatchesTerms(v.Name, Param("name"))
                && SpoolmanQuery.MatchesTerms(v.ExternalId, Param("external_id")));
            var sorted = SpoolmanQuery.Sort(matches, Request.Query["sort"].FirstOrDefault(), SpoolmanQuery.VendorField, v => v.Id);
            return Page(sorted, SpoolmanJsonContext.Wire.IReadOnlyListSpoolmanVendor);
        }
        catch (SpoolmanQueryException ex)
        {
            return Message(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    [HttpGet("vendor/{id:int}")]
    public async Task<IActionResult> GetVendor(int id) =>
        (await InventoryAsync()).Vendors.FirstOrDefault(v => v.Id == id) is { } vendor
            ? Json(vendor, SpoolmanJsonContext.Wire.SpoolmanVendor)
            : Message(StatusCodes.Status404NotFound, $"No vendor with ID {id} found.");

    [HttpGet("location")]
    public async Task<IActionResult> Locations() => Strings((await InventoryAsync()).Spools.Select(s => s.Location));

    [HttpGet("material")]
    public async Task<IActionResult> Materials() => Strings((await InventoryAsync()).Spools.Select(s => s.Filament.Material));

    /// <summary>3D Print Log records no lot numbers.</summary>
    [HttpGet("lot-number")]
    public IActionResult LotNumbers() => Strings([]);

    /// <summary>3D Print Log records no article numbers.</summary>
    [HttpGet("article-number")]
    public IActionResult ArticleNumbers() => Strings([]);

    /// <summary>Every Spoolman setting, with the user's currency and Spoolman's defaults otherwise.</summary>
    [HttpGet("setting")]
    public async Task<IActionResult> GetSettings()
    {
        var currency = await CurrencyAsync();
        var all = Settings.ToDictionary(s => s.Key, s => s.Key == "currency" && currency is not null
            ? new SpoolmanSettingResponse(JsonSerializer.Serialize(currency), IsSet: true, s.Type)
            : new SpoolmanSettingResponse(s.Default, IsSet: false, s.Type));
        return Json<IReadOnlyDictionary<string, SpoolmanSettingResponse>>(all, SpoolmanJsonContext.Wire.IReadOnlyDictionaryStringSpoolmanSettingResponse);
    }

    /// <summary>One setting. Fluidd reads <c>currency</c>; the value is JSON-encoded (<c>"\"USD\""</c>).</summary>
    [HttpGet("setting/{key}")]
    public async Task<IActionResult> GetSetting(string key)
    {
        if (Settings.FirstOrDefault(s => s.Key == key) is not { Key: not null } setting)
        {
            return Message(StatusCodes.Status404NotFound, $"Setting '{key}' does not exist.");
        }

        var currency = key == "currency" ? await CurrencyAsync() : null;
        var response = currency is not null
            ? new SpoolmanSettingResponse(JsonSerializer.Serialize(currency), IsSet: true, setting.Type)
            : new SpoolmanSettingResponse(setting.Default, IsSet: false, setting.Type);
        return Json(response, SpoolmanJsonContext.Wire.SpoolmanSettingResponse);
    }

    /// <summary>Extra fields. None in phase 1 (#172).</summary>
    [HttpGet("field/{entity}")]
    public IActionResult Fields(string entity) =>
        FieldEntities.Contains(entity)
            ? Strings([])
            : Message(StatusCodes.Status404NotFound, $"Unknown entity type '{entity}'.");

    /// <summary>
    /// Every write Spoolman has, until #172: creating, editing and deleting spools, filaments and
    /// vendors, <c>/measure</c>, settings and extra fields. A route the read endpoints above match
    /// more specifically never reaches this.
    /// <para>GET is included only so an unknown read is a 404, as Spoolman answers it: a catch-all
    /// for the other methods alone would turn every unknown GET into a 405.</para>
    /// </summary>
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE")]
    [Route("{**path}")]
    public IActionResult NotSupported() => HttpMethods.IsGet(Request.Method)
        ? Message(StatusCodes.Status404NotFound, "Not found.")
        : Message(StatusCodes.Status400BadRequest, NotSupportedMessage);

    private long UserId() => User.GetUserId()
        ?? throw new InvalidOperationException("The Spoolman policy guarantees an authenticated user.");

    private Task<SpoolmanInventory> InventoryAsync() => inventory.GetInventoryAsync(UserId(), HttpContext.RequestAborted);

    /// <summary>The user's currency (Settings, Currency_Name), when they have set a three-letter code.</summary>
    private async Task<string?> CurrencyAsync()
    {
        var userId = UserId();
        var value = await context.UserSettings.AsNoTracking()
            .Where(s => s.UserId == userId && s.UserSettingTypeId == CurrencySettingTypeId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(HttpContext.RequestAborted);
        return value?.Trim() is { Length: 3 } code && code.All(char.IsAsciiLetter) ? code.ToUpperInvariant() : null;
    }

    /// <summary>The first of the names that is present, so the deprecated spellings still filter.</summary>
    private string? Param(params string[] names)
    {
        foreach (var name in names)
        {
            if (Request.Query.TryGetValue(name, out var value))
            {
                return value.ToString();
            }
        }

        return null;
    }

    private bool Bool(string name) =>
        Request.Query.TryGetValue(name, out var value) && value.ToString().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    private IActionResult Page<T>(IReadOnlyList<T> sorted, JsonTypeInfo<IReadOnlyList<T>> typeInfo)
    {
        int? limit = int.TryParse(Request.Query["limit"].FirstOrDefault(), out var l) ? l : null;
        var offset = int.TryParse(Request.Query["offset"].FirstOrDefault(), out var o) ? o : 0;
        var page = SpoolmanQuery.Page(sorted, limit, offset);
        Response.Headers["x-total-count"] = sorted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Json(page, typeInfo);
    }

    private IActionResult Strings(IEnumerable<string?> values) => Json<IReadOnlyList<string>>(
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList(),
        SpoolmanJsonContext.Wire.IReadOnlyListString);

    private ContentResult Json<T>(T value, JsonTypeInfo<T> typeInfo) => new()
    {
        Content = JsonSerializer.Serialize(value, typeInfo),
        ContentType = "application/json",
        StatusCode = StatusCodes.Status200OK,
    };

    private ContentResult Message(int status, string message) => new()
    {
        Content = JsonSerializer.Serialize(new SpoolmanMessage(message), SpoolmanJsonContext.Wire.SpoolmanMessage),
        ContentType = "application/json",
        StatusCode = status,
    };
}
