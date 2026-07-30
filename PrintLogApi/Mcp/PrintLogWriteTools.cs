using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PrintLogApi.Enums;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.Mcp
{
    /// <summary>
    /// The write MCP tool surface. Every tool runs as the token-derived user (resolved from the
    /// token, never a tool argument), enforces ownership in the service query, bounds its blast
    /// radius on the server, and never mutates printer loaded-state. The class-level
    /// <see cref="AuthorizeAttribute"/> requires the write:printdata scope on top of the endpoint's
    /// authentication policy.
    /// </summary>
    [McpServerToolType]
    [Authorize(Policy = "McpWrite")]
    public class PrintLogWriteTools
    {
        private readonly IHttpContextAccessor httpContextAccessor;
        private readonly IPrintService printService;
        private readonly IFilamentService filamentService;
        private readonly IProjectService projectService;
        private readonly IPrinterService printerService;
        private readonly IFeedbackService feedbackService;

        public PrintLogWriteTools(
            IHttpContextAccessor httpContextAccessor,
            IPrintService printService,
            IFilamentService filamentService,
            IProjectService projectService,
            IPrinterService printerService,
            IFeedbackService feedbackService)
        {
            this.httpContextAccessor = httpContextAccessor;
            this.printService = printService;
            this.filamentService = filamentService;
            this.projectService = projectService;
            this.printerService = printerService;
            this.feedbackService = feedbackService;
        }

        private long CurrentUserId =>
            McpUserContext.RequireUserId(httpContextAccessor.HttpContext!.User);

        /// <summary>Upper bound on material-usage rows in one print write, so a single rate-limited
        /// call cannot submit an unbounded array. Far above any realistic multi-material print.</summary>
        private const int MaxMaterialRows = 50;

        /// <summary>The nullable print fields update_print will clear on request.</summary>
        public static readonly HashSet<string> ClearablePrintFields = new()
        {
            "fileName", "url", "notes", "startedAt", "estimatedDurationSeconds", "durationSeconds", "projectId",
        };

        /// <summary>
        /// A usage row must carry an actual pair, an estimated pair, or both; a source without its
        /// amount (or vice versa) is a half-specified measurement and is rejected rather than guessed.
        /// </summary>
        public static void ValidateUsageRow(MaterialUsageInput row)
        {
            bool hasActual = row.Source.HasValue || row.Amount.HasValue;
            bool hasEstimated = row.EstimatedSource.HasValue || row.EstimatedAmount.HasValue;
            if (!hasActual && !hasEstimated)
            {
                throw McpToolException.InvalidArguments("Each material row needs an actual and/or an estimated amount.");
            }
            if (row.Source.HasValue != row.Amount.HasValue)
            {
                throw McpToolException.InvalidArguments("A material row's source and amount must be provided together.");
            }
            if (row.EstimatedSource.HasValue != row.EstimatedAmount.HasValue)
            {
                throw McpToolException.InvalidArguments("A material row's estimatedSource and estimatedAmount must be provided together.");
            }
            if (row.Source.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(row.Source.Value, "materials.source");
                McpWriteValidation.RequirePositiveAmount(row.Amount.Value);
            }
            if (row.EstimatedSource.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(row.EstimatedSource.Value, "materials.estimatedSource");
                McpWriteValidation.RequirePositiveAmount(row.EstimatedAmount.Value);
            }
            McpWriteValidation.RequireMaxLength(row.Notes, 1000, "materials.notes");
        }

        [McpServerTool(Name = "whoami", Title = "Who Am I", ReadOnly = true, OpenWorld = false),
         Description("Confirms write access is granted. Returns your internal user id.")]
        public long WhoAmI() => CurrentUserId;

        [McpServerTool(Name = "create_print", Title = "Create Print", Idempotent = true, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Log a finished 3D print for yourself. Records status, optional start time, actual and " +
            "estimated duration (seconds), notes, file name, url, visibility, and per-material usage. " +
            "Each usage row is { materialId, source, amount, estimatedSource, estimatedAmount, notes } " +
            "with a source of Weight (grams), Length (mm), or Volume (ml); provide an actual pair, an " +
            "estimated pair, or both. viewStatus/allowComments default to your account settings when " +
            "omitted; allowFileDownloads defaults to false. 'idempotencyKey' MUST be a stable id for " +
            "this physical print: reusing it with the SAME arguments returns the same print " +
            "(wasReplayed = true); reusing it with DIFFERENT arguments is a conflict. Does NOT change " +
            "which spools are loaded. Only your own printer/materials/project are accepted; anything " +
            "else is 'not found'.")]
        public async Task<CreatePrintResult> CreatePrint(
            [Description("Print title (max 100 chars).")] string title,
            [Description("Your printer id (see list_printers).")] long printerId,
            [Description("Print status, e.g. Success, PartialSuccess, Failed.")] Print.PrintStatus status,
            [Description("Stable idempotency key for this print.")] string idempotencyKey,
            [Description("Optional UTC start time.")] DateTimeOffset? startedAt = null,
            [Description("Optional measured duration in seconds (> 0).")] int? durationSeconds = null,
            [Description("Optional estimated duration in seconds (> 0).")] int? estimatedDurationSeconds = null,
            [Description("Optional notes (max 50000).")] string notes = null,
            [Description("Optional project id (see list_projects).")] Guid? projectId = null,
            [Description("Optional source file name (max 1000).")] string fileName = null,
            [Description("Optional url (max 1000).")] string url = null,
            [Description("Optional visibility. Defaults to your account default.")] Print.PrintViewStatus? viewStatus = null,
            [Description("Optional. Defaults to your account default.")] bool? allowComments = null,
            [Description("Optional. Defaults to false.")] bool? allowFileDownloads = null,
            [Description("Optional per-material usage rows.")] MaterialUsageInput[] materials = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                throw McpToolException.InvalidArguments("title is required.");
            }
            McpWriteValidation.RequireMaxLength(title, 100, "title");
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw McpToolException.InvalidArguments("idempotencyKey is required.");
            }
            McpWriteValidation.RequireMaxLength(idempotencyKey, 200, "idempotencyKey");
            McpWriteValidation.RequireDefinedEnum(status, "status");
            if (viewStatus.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(viewStatus.Value, "viewStatus");
            }
            McpWriteValidation.RequireMaxLength(notes, 50000, "notes");
            McpWriteValidation.RequireMaxLength(fileName, 1000, "fileName");
            McpWriteValidation.RequireMaxLength(url, 1000, "url");
            if (durationSeconds.HasValue)
            {
                McpWriteValidation.RequirePositiveDuration(durationSeconds.Value, "durationSeconds");
            }
            if (estimatedDurationSeconds.HasValue)
            {
                McpWriteValidation.RequirePositiveDuration(estimatedDurationSeconds.Value, "estimatedDurationSeconds");
            }

            var rows = materials ?? Array.Empty<MaterialUsageInput>();
            if (rows.Length > MaxMaterialRows)
            {
                throw McpToolException.InvalidArguments($"At most {MaxMaterialRows} material rows are allowed.");
            }
            foreach (var row in rows)
            {
                ValidateUsageRow(row);
            }

            return await printService.CreatePrintForMcp(
                CurrentUserId, title, printerId, status, startedAt, durationSeconds, estimatedDurationSeconds,
                notes, projectId, fileName?.Trim(), url?.Trim(), viewStatus, allowComments, allowFileDownloads,
                rows, idempotencyKey.Trim(), ct);
        }

        [McpServerTool(Name = "update_print", Title = "Update Print", Idempotent = false, Destructive = true, ReadOnly = false, OpenWorld = false),
         Description(
            "Edit one of your own prints. Only fields you pass are changed. To clear a nullable field, " +
            "list its name in 'clear' (fileName, url, notes, startedAt, durationSeconds, " +
            "estimatedDurationSeconds, projectId). Passing 'materials' REPLACES the entire usage list. " +
            "Only the print's creator can edit it; any other id is 'not found'.")]
        public async Task<PrintDetailResult> UpdatePrint(
            [Description("The print id.")] long id,
            [Description("Optional new title (max 100).")] string title = null,
            [Description("Optional new status.")] Print.PrintStatus? status = null,
            [Description("Optional new notes (max 50000).")] string notes = null,
            [Description("Optional new UTC start time.")] DateTimeOffset? startedAt = null,
            [Description("Optional new printer id (must be yours).")] long? printerId = null,
            [Description("Optional new duration seconds (> 0).")] int? durationSeconds = null,
            [Description("Optional new estimated duration seconds (> 0).")] int? estimatedDurationSeconds = null,
            [Description("Optional new file name (max 1000).")] string fileName = null,
            [Description("Optional new url (max 1000).")] string url = null,
            [Description("Optional new visibility.")] Print.PrintViewStatus? viewStatus = null,
            [Description("Optional.")] bool? allowComments = null,
            [Description("Optional.")] bool? allowFileDownloads = null,
            [Description("Optional project id to file under.")] Guid? projectId = null,
            [Description("Optional field names to clear.")] string[] clear = null,
            [Description("Optional replacement material-usage list. Omit to leave unchanged.")] MaterialUsageInput[] materials = null,
            CancellationToken ct = default)
        {
            if (title != null)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    throw McpToolException.InvalidArguments("title cannot be empty.");
                }
                McpWriteValidation.RequireMaxLength(title, 100, "title");
            }
            if (status.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(status.Value, "status");
            }
            if (viewStatus.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(viewStatus.Value, "viewStatus");
            }
            McpWriteValidation.RequireMaxLength(notes, 50000, "notes");
            McpWriteValidation.RequireMaxLength(fileName, 1000, "fileName");
            McpWriteValidation.RequireMaxLength(url, 1000, "url");
            if (durationSeconds.HasValue)
            {
                McpWriteValidation.RequirePositiveDuration(durationSeconds.Value, "durationSeconds");
            }
            if (estimatedDurationSeconds.HasValue)
            {
                McpWriteValidation.RequirePositiveDuration(estimatedDurationSeconds.Value, "estimatedDurationSeconds");
            }
            var clearFields = McpWriteValidation.RequireAllowedClearFields(clear, ClearablePrintFields);

            var materialsProvided = materials != null;
            if (materialsProvided)
            {
                if (materials.Length > MaxMaterialRows)
                {
                    throw McpToolException.InvalidArguments($"At most {MaxMaterialRows} material rows are allowed.");
                }
                foreach (var row in materials)
                {
                    ValidateUsageRow(row);
                }
            }

            return await printService.UpdateOwnPrintForMcp(
                CurrentUserId, id, title, status, notes, startedAt, printerId, durationSeconds, estimatedDurationSeconds,
                fileName, url, viewStatus, allowComments, allowFileDownloads, projectId,
                materialsProvided, materials ?? Array.Empty<MaterialUsageInput>(), clearFields, ct);
        }

        [McpServerTool(Name = "create_material", Title = "Create Material", Idempotent = false, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Add a new material to your inventory (filament, resin, powder, etc.). 'source' is how the " +
            "initial amount is measured: Weight (grams), Length (mm), or Volume (ml) — it names the " +
            "AUTHORITATIVE figure; everything else is derived from it. 'materialCategoryNickname' must " +
            "be one of your existing categories (e.g. 'filament', 'resin'); an unknown category is " +
            "rejected, never silently replaced. Categories that track a diameter require diameterMm. " +
            "Colors are 6 hex digits with no leading '#': pass 'colorHex' for a single color OR " +
            "'colors' for multiple (colors[0] wins if both are given; an empty colors array means no " +
            "color). Temperatures are °C, cure times seconds, weights grams. 'idempotencyKey' is " +
            "OPTIONAL but recommended: with one, retrying with the SAME arguments returns the same " +
            "material (wasReplayed = true) and reusing it with DIFFERENT arguments is a conflict; " +
            "WITHOUT one, a retried call creates a SECOND material.")]
        public async Task<CreateMaterialResult> CreateMaterial(
            [Description("Display name (max 255).")] string displayName,
            [Description("Material type, e.g. PLA, ABS, Resin (max 255).")] string materialType,
            [Description("Category nickname, e.g. filament or resin (max 50).")] string materialCategoryNickname,
            [Description("Density in g/cm^3 (> 0).")] double densityGramPerCubicCm,
            [Description("How the initial amount is measured.")] McpMeasurementSource source,
            [Description("Initial amount in the source's unit (g / mm / ml).")] double initialAmount,
            [Description("Diameter in mm (> 0). Required for diameter-tracking categories.")] double? diameterMm = null,
            [Description("Optional brand (max 255).")] string brand = null,
            [Description("Optional color name (max 255).")] string colorName = null,
            [Description("Optional single color as 6 hex digits, no '#', e.g. 1188FF.")] string colorHex = null,
            [Description("Optional multi-color swatches (max 32); colors[0] becomes the primary color.")] string[] colors = null,
            [Description("Optional color pattern: Solid, Multi, Gradient, Rainbow.")] ColorPatternType? colorPattern = null,
            [Description("Optional finish: Standard, Silk, Matte.")] FilamentFinishType? finishType = null,
            [Description("Optional effects, e.g. Sparkle, GlowInDark, CarbonFiber.")] FilamentEffect[] effects = null,
            [Description("Optional storage location (max 256).")] string storageLocation = null,
            [Description("Whether the material is active. Defaults to true.")] bool? isActive = null,
            [Description("Optional favorite flag. Defaults to false.")] bool? isFavorite = null,
            [Description("Optional notes (max 1000).")] string notes = null,
            [Description("Optional empty-spool weight in grams (>= 0).")] double? spoolWeightGrams = null,
            [Description("Optional on-scale weight in grams incl. spool (>= 0).")] double? initialTotalWeightGrams = null,
            [Description("Optional lower print temperature in °C.")] double? tempRangeStartC = null,
            [Description("Optional upper print temperature in °C (>= tempRangeStartC).")] double? tempRangeEndC = null,
            [Description("Optional recommended hotend temperature in °C.")] double? recommendedTempC = null,
            [Description("Optional recommended bed temperature in °C.")] double? recommendedBedTempC = null,
            [Description("Optional resin initial-layer cure time in seconds (>= 0).")] double? initialLayerTimeS = null,
            [Description("Optional resin layer cure time in seconds (>= 0).")] double? layerTimeS = null,
            [Description("Optional melting temperature in °C.")] double? meltingTemperatureC = null,
            [Description("Optional inert gas for powder processes (max 255).")] string inertGas = null,
            [Description("Optional powder refresh ratio, 0.0 to 1.0.")] double? materialRefreshRatio = null,
            [Description("Optional UTC purchase date.")] DateTimeOffset? purchaseDate = null,
            [Description("Optional purchase location or URL (max 1000).")] string purchaseLocation = null,
            [Description("Optional purchase price as text, e.g. '24.99' (max 256).")] string purchasePriceValue = null,
            [Description("Optional currency marker, e.g. USD (max 256).")] string purchasePriceCurrency = null,
            [Description("Optional purchase notes (max 1000).")] string purchaseNotes = null,
            [Description("Optional stable key making a retry safe. Strongly recommended.")] string idempotencyKey = null,
            CancellationToken ct = default)
        {
            var input = new MaterialAttributesInput
            {
                DisplayName = displayName,
                MaterialType = materialType,
                MaterialCategoryNickname = materialCategoryNickname,
                DensityGramPerCubicCm = densityGramPerCubicCm,
                DiameterMm = diameterMm,
                Source = source,
                InitialAmount = initialAmount,
                Brand = brand,
                ColorName = colorName,
                ColorHex = colorHex,
                Colors = colors,
                ColorPattern = colorPattern,
                FinishType = finishType,
                Effects = effects,
                StorageLocation = storageLocation,
                IsActive = isActive,
                IsFavorite = isFavorite,
                Notes = notes,
                SpoolWeightGrams = spoolWeightGrams,
                InitialTotalWeightGrams = initialTotalWeightGrams,
                TempRangeStartC = tempRangeStartC,
                TempRangeEndC = tempRangeEndC,
                RecommendedTempC = recommendedTempC,
                RecommendedBedTempC = recommendedBedTempC,
                InitialLayerTimeS = initialLayerTimeS,
                LayerTimeS = layerTimeS,
                MeltingTemperatureC = meltingTemperatureC,
                InertGas = inertGas,
                MaterialRefreshRatio = materialRefreshRatio,
                PurchaseDate = purchaseDate,
                PurchaseLocation = purchaseLocation,
                PurchasePriceValue = purchasePriceValue,
                PurchasePriceCurrency = purchasePriceCurrency,
                PurchaseNotes = purchaseNotes,
            };

            return await filamentService.CreateMaterialForMcp(CurrentUserId, input, idempotencyKey, ct);
        }

        [McpServerTool(Name = "update_material", Title = "Update Material", Idempotent = false, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Edit one of your own materials. Only fields you pass are changed. To clear a nullable " +
            "field, list its name in 'clear' (brand, colorName, colorHex, colors, storageLocation, " +
            "notes, purchaseLocation, purchasePriceValue, purchasePriceCurrency, purchaseNotes, " +
            "inertGas, purchaseDate, spoolWeightGrams, initialTotalWeightGrams, diameterMm, " +
            "tempRangeStartC, tempRangeEndC, recommendedTempC, recommendedBedTempC, initialLayerTimeS, " +
            "layerTimeS, meltingTemperatureC, materialRefreshRatio, colorPattern, finishType, " +
            "effects); clearing colorHex or colors clears both. 'source' and 'initialAmount' must be " +
            "given together and REBASE the capacity. NOTE: the source amount is authoritative and " +
            "weight is derived from it, so editing density or diameter on a Length/Volume material " +
            "recomputes its capacity in grams and hence how much it reports as remaining — use " +
            "adjust_material_remaining to change quantity without rebasing capacity. Materials " +
            "belonging to anyone else are 'not found'.")]
        public async Task<MaterialDetail> UpdateMaterial(
            [Description("The material id.")] Guid materialId,
            [Description("Optional new display name (max 255).")] string displayName = null,
            [Description("Optional new material type (max 255).")] string materialType = null,
            [Description("Optional new category nickname (max 50).")] string materialCategoryNickname = null,
            [Description("Optional new density in g/cm^3 (> 0).")] double? densityGramPerCubicCm = null,
            [Description("Optional new diameter in mm (> 0).")] double? diameterMm = null,
            [Description("Optional new source. Must accompany initialAmount.")] McpMeasurementSource? source = null,
            [Description("Optional new initial amount in the source's unit. Must accompany source.")] double? initialAmount = null,
            [Description("Optional new brand (max 255).")] string brand = null,
            [Description("Optional new color name (max 255).")] string colorName = null,
            [Description("Optional new single color, 6 hex digits, no '#'.")] string colorHex = null,
            [Description("Optional new color swatches (max 32); colors[0] becomes the primary color.")] string[] colors = null,
            [Description("Optional new color pattern.")] ColorPatternType? colorPattern = null,
            [Description("Optional new finish.")] FilamentFinishType? finishType = null,
            [Description("Optional replacement effects list.")] FilamentEffect[] effects = null,
            [Description("Optional new storage location (max 256).")] string storageLocation = null,
            [Description("Optional active flag.")] bool? isActive = null,
            [Description("Optional favorite flag.")] bool? isFavorite = null,
            [Description("Optional new notes (max 1000).")] string notes = null,
            [Description("Optional new empty-spool weight in grams (>= 0).")] double? spoolWeightGrams = null,
            [Description("Optional new on-scale weight in grams incl. spool (>= 0).")] double? initialTotalWeightGrams = null,
            [Description("Optional new lower print temperature in °C.")] double? tempRangeStartC = null,
            [Description("Optional new upper print temperature in °C.")] double? tempRangeEndC = null,
            [Description("Optional new recommended hotend temperature in °C.")] double? recommendedTempC = null,
            [Description("Optional new recommended bed temperature in °C.")] double? recommendedBedTempC = null,
            [Description("Optional new resin initial-layer cure time in seconds (>= 0).")] double? initialLayerTimeS = null,
            [Description("Optional new resin layer cure time in seconds (>= 0).")] double? layerTimeS = null,
            [Description("Optional new melting temperature in °C.")] double? meltingTemperatureC = null,
            [Description("Optional new inert gas (max 255).")] string inertGas = null,
            [Description("Optional new powder refresh ratio, 0.0 to 1.0.")] double? materialRefreshRatio = null,
            [Description("Optional new UTC purchase date.")] DateTimeOffset? purchaseDate = null,
            [Description("Optional new purchase location or URL (max 1000).")] string purchaseLocation = null,
            [Description("Optional new purchase price as text (max 256).")] string purchasePriceValue = null,
            [Description("Optional new currency marker (max 256).")] string purchasePriceCurrency = null,
            [Description("Optional new purchase notes (max 1000).")] string purchaseNotes = null,
            [Description("Optional field names to clear.")] string[] clear = null,
            CancellationToken ct = default)
        {
            var clearFields = McpWriteValidation.RequireAllowedClearFields(
                clear, new HashSet<string>(McpMaterialValidation.ClearableFields));

            var input = new MaterialAttributesInput
            {
                DisplayName = displayName,
                MaterialType = materialType,
                MaterialCategoryNickname = materialCategoryNickname,
                DensityGramPerCubicCm = densityGramPerCubicCm,
                DiameterMm = diameterMm,
                Source = source,
                InitialAmount = initialAmount,
                Brand = brand,
                ColorName = colorName,
                ColorHex = colorHex,
                Colors = colors,
                ColorPattern = colorPattern,
                FinishType = finishType,
                Effects = effects,
                StorageLocation = storageLocation,
                IsActive = isActive,
                IsFavorite = isFavorite,
                Notes = notes,
                SpoolWeightGrams = spoolWeightGrams,
                InitialTotalWeightGrams = initialTotalWeightGrams,
                TempRangeStartC = tempRangeStartC,
                TempRangeEndC = tempRangeEndC,
                RecommendedTempC = recommendedTempC,
                RecommendedBedTempC = recommendedBedTempC,
                InitialLayerTimeS = initialLayerTimeS,
                LayerTimeS = layerTimeS,
                MeltingTemperatureC = meltingTemperatureC,
                InertGas = inertGas,
                MaterialRefreshRatio = materialRefreshRatio,
                PurchaseDate = purchaseDate,
                PurchaseLocation = purchaseLocation,
                PurchasePriceValue = purchasePriceValue,
                PurchasePriceCurrency = purchasePriceCurrency,
                PurchaseNotes = purchaseNotes,
            };

            return await filamentService.UpdateOwnMaterialForMcp(CurrentUserId, materialId, input, clearFields, ct);
        }

        [McpServerTool(Name = "create_printer", Title = "Create Printer", Idempotent = false, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Add a printer to your account. 'make', 'model' and 'name' are required. " +
            "'categoryNickname' must be one of the known printer categories (e.g. FFF, FDM, SLA, " +
            "SLS); an unknown one is rejected, never silently replaced, and omitting it uses FFF. " +
            "Dimensions are millimetres, wattage is watts, screen resolutions are pixels — all " +
            "stored exactly as given, with no conversion. A new printer is active unless you pass " +
            "isActive = false. This tool never loads or unloads filament: use the load/unload flow " +
            "for that. 'idempotencyKey' is OPTIONAL but recommended: with one, retrying with the " +
            "SAME arguments returns the same printer (wasReplayed = true) and reusing it with " +
            "DIFFERENT arguments is a conflict; WITHOUT one, a retried call creates a SECOND printer.")]
        public async Task<CreatePrinterResult> CreatePrinter(
            [Description("Manufacturer, e.g. Bambu Lab (max 50).")] string make,
            [Description("Model, e.g. X1 Carbon (max 50).")] string model,
            [Description("Your name for this printer (max 100).")] string name,
            [Description("Optional description (max 1000).")] string description = null,
            [Description("Optional category nickname, e.g. FFF or SLA (max 50). Defaults to FFF.")] string categoryNickname = null,
            [Description("Optional nozzle diameter in mm (>= 0).")] double? nozzleDiameterMm = null,
            [Description("Optional filament diameter in mm (>= 0).")] double? filamentDiameterMm = null,
            [Description("Optional laser beam diameter in mm (>= 0).")] double? beamDiameterMm = null,
            [Description("Optional bed width in mm (>= 0).")] double? bedWidthMm = null,
            [Description("Optional bed depth in mm (>= 0).")] double? bedDepthMm = null,
            [Description("Optional build height in mm (>= 0).")] double? bedHeightMm = null,
            [Description("Optional screen width in pixels (>= 0).")] double? screenResolutionXPixels = null,
            [Description("Optional screen height in pixels (>= 0).")] double? screenResolutionYPixels = null,
            [Description("Optional heated-bed flag.")] bool? hasHeatedBed = null,
            [Description("Optional heated-chamber flag.")] bool? hasHeatedChamber = null,
            [Description("Optional power draw in watts (>= 0).")] double? wattageW = null,
            [Description("Whether the printer is in use. Defaults to true.")] bool? isActive = null,
            [Description("Optional stable key making a retry safe. Strongly recommended.")] string idempotencyKey = null,
            CancellationToken ct = default)
        {
            return await printerService.CreatePrinterForMcp(
                CurrentUserId, BuildPrinterInput(
                    make, model, name, description, categoryNickname, nozzleDiameterMm, filamentDiameterMm,
                    beamDiameterMm, bedWidthMm, bedDepthMm, bedHeightMm, screenResolutionXPixels,
                    screenResolutionYPixels, hasHeatedBed, hasHeatedChamber, wattageW, isActive),
                idempotencyKey, ct);
        }

        // Destructive = true, matching update_print: this tool overwrites fields and honours 'clear',
        // so a retry is not free and a client must be able to reason about that.
        [McpServerTool(Name = "update_printer", Title = "Update Printer", Idempotent = false, Destructive = true, ReadOnly = false, OpenWorld = false),
         Description(
            "Edit one of your own printers. Only fields you pass are changed. To clear a nullable " +
            "field, list its name in 'clear' (description, nozzleDiameterMm, filamentDiameterMm, " +
            "beamDiameterMm, bedWidthMm, bedDepthMm, bedHeightMm, screenResolutionXPixels, " +
            "screenResolutionYPixels, hasHeatedBed, hasHeatedChamber, wattageW). make, model, name, " +
            "isActive and categoryNickname cannot be cleared. This tool never loads or unloads " +
            "filament — the printer's loaded spools are returned but not changed. Printers belonging " +
            "to anyone else are 'not found'.")]
        public async Task<PrinterDetailResult> UpdatePrinter(
            [Description("The printer id (see list_printers).")] long id,
            [Description("Optional new manufacturer (max 50).")] string make = null,
            [Description("Optional new model (max 50).")] string model = null,
            [Description("Optional new name (max 100).")] string name = null,
            [Description("Optional new description (max 1000).")] string description = null,
            [Description("Optional new category nickname (max 50).")] string categoryNickname = null,
            [Description("Optional new nozzle diameter in mm (>= 0).")] double? nozzleDiameterMm = null,
            [Description("Optional new filament diameter in mm (>= 0).")] double? filamentDiameterMm = null,
            [Description("Optional new laser beam diameter in mm (>= 0).")] double? beamDiameterMm = null,
            [Description("Optional new bed width in mm (>= 0).")] double? bedWidthMm = null,
            [Description("Optional new bed depth in mm (>= 0).")] double? bedDepthMm = null,
            [Description("Optional new build height in mm (>= 0).")] double? bedHeightMm = null,
            [Description("Optional new screen width in pixels (>= 0).")] double? screenResolutionXPixels = null,
            [Description("Optional new screen height in pixels (>= 0).")] double? screenResolutionYPixels = null,
            [Description("Optional heated-bed flag.")] bool? hasHeatedBed = null,
            [Description("Optional heated-chamber flag.")] bool? hasHeatedChamber = null,
            [Description("Optional new power draw in watts (>= 0).")] double? wattageW = null,
            [Description("Optional active flag.")] bool? isActive = null,
            [Description("Optional field names to clear.")] string[] clear = null,
            CancellationToken ct = default)
        {
            var clearFields = McpWriteValidation.RequireAllowedClearFields(
                clear, new HashSet<string>(McpPrinterValidation.ClearableFields));

            return await printerService.UpdatePrinterForMcp(
                CurrentUserId, id, BuildPrinterInput(
                    make, model, name, description, categoryNickname, nozzleDiameterMm, filamentDiameterMm,
                    beamDiameterMm, bedWidthMm, bedDepthMm, bedHeightMm, screenResolutionXPixels,
                    screenResolutionYPixels, hasHeatedBed, hasHeatedChamber, wattageW, isActive),
                clearFields, ct);
        }

        /// <summary>
        /// Shared by create_printer and update_printer so the two parameter lists cannot drift into
        /// mapping the same argument to different fields.
        /// </summary>
        private static PrinterAttributesInput BuildPrinterInput(
            string make, string model, string name, string description, string categoryNickname,
            double? nozzleDiameterMm, double? filamentDiameterMm, double? beamDiameterMm,
            double? bedWidthMm, double? bedDepthMm, double? bedHeightMm,
            double? screenResolutionXPixels, double? screenResolutionYPixels,
            bool? hasHeatedBed, bool? hasHeatedChamber, double? wattageW, bool? isActive) => new()
        {
            Make = make,
            Model = model,
            Name = name,
            Description = description,
            CategoryNickname = categoryNickname,
            NozzleDiameterMm = nozzleDiameterMm,
            FilamentDiameterMm = filamentDiameterMm,
            BeamDiameterMm = beamDiameterMm,
            BedWidthMm = bedWidthMm,
            BedDepthMm = bedDepthMm,
            BedHeightMm = bedHeightMm,
            ScreenResolutionXPixels = screenResolutionXPixels,
            ScreenResolutionYPixels = screenResolutionYPixels,
            HasHeatedBed = hasHeatedBed,
            HasHeatedChamber = hasHeatedChamber,
            WattageW = wattageW,
            IsActive = isActive,
        };

        // Destructive = false: the change is a bounded, reversible delta on one quantity — the
        // inverse delta restores it. Idempotent = false: replaying it applies the delta twice.
        [McpServerTool(Title = "Adjust Material Remaining", Idempotent = false, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Correct how much of one of your materials remains, by applying a delta (positive adds, " +
            "negative removes) measured as Weight (grams), Length (mm), or Volume (ml). The result " +
            "cannot go below zero or above the material's original capacity — an out-of-range " +
            "adjustment is rejected. Returns 'beforeGrams' and 'afterGrams' — the remaining amount is " +
            "always reported in GRAMS, whichever unit you sent the delta in. Foreign materials " +
            "are 'not found'.")]
        public async Task<MaterialWriteResult> AdjustMaterialRemaining(
            [Description("The material id.")] Guid materialId,
            [Description("Unit of the delta.")] McpMeasurementSource source,
            [Description("Signed delta in the unit (g / mm / ml). Negative removes.")] double delta,
            [Description("Optional note explaining the adjustment.")] string notes = null,
            CancellationToken ct = default)
        {
            McpWriteValidation.RequireDefinedEnum(source, "source");
            McpWriteValidation.RequireMaxLength(notes, 1000, "notes");
            return await filamentService.AdjustMaterialRemainingForMcp(CurrentUserId, materialId, source, delta, notes, ct);
        }

        // Idempotent = true: setting the flag to a value it already holds is a no-op, so a retry is
        // free. Destructive = false: retiring hides the material but keeps all of its history.
        [McpServerTool(Title = "Set Material Active", Idempotent = true, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Activate or retire one of your materials. Retiring hides it from default inventory " +
            "listings but keeps its history. Foreign materials are 'not found'.")]
        public async Task<MaterialInventoryItem> SetMaterialActive(
            [Description("The material id.")] Guid materialId,
            [Description("True to activate, false to retire.")] bool isActive,
            CancellationToken ct = default)
        {
            return await filamentService.SetMaterialActiveForMcp(CurrentUserId, materialId, isActive, ct);
        }

        [McpServerTool(Name = "create_project", Title = "Create Project", Idempotent = false, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Create a new project to group prints under. Name is required (max 100 chars). viewStatus " +
            "controls visibility (Private, Unlisted, Public) and defaults to Private; the result echoes " +
            "every field it stored. 'idempotencyKey' is OPTIONAL but recommended: with one, retrying " +
            "with the SAME arguments returns the same project (wasReplayed = true) and reusing it with " +
            "DIFFERENT arguments is a conflict; WITHOUT one, a retried call creates a SECOND project.")]
        public async Task<CreateProjectResult> CreateProject(
            [Description("Project name (max 100 chars).")] string name,
            [Description("Optional external reference (max 100 chars).")] string reference = null,
            [Description("Optional description (max 5000 chars).")] string description = null,
            [Description("Optional URL (max 1000 chars).")] string url = null,
            [Description("Status, default InProgress.")] Project.ProjectStatus status = Project.ProjectStatus.InProgress,
            [Description("Visibility, default Private.")] Project.ProjectViewStatus viewStatus = Project.ProjectViewStatus.Private,
            [Description("Optional stable key making a retry safe. Strongly recommended.")] string idempotencyKey = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw McpToolException.InvalidArguments("name is required.");
            }
            ValidateProjectFields(name, reference, description, url, status, viewStatus);
            return await projectService.CreateProjectForMcp(
                CurrentUserId, name, reference, description, url, status, viewStatus, idempotencyKey, ct);
        }

        // Destructive = true, matching the other update_* tools: passing a field overwrites whatever
        // it held, and the previous value is not recoverable through any tool.
        [McpServerTool(Title = "Update Project", Idempotent = false, Destructive = true, ReadOnly = false, OpenWorld = false),
         Description(
            "Edit one of your own projects. Only fields you pass are changed. viewStatus changes " +
            "visibility; the result echoes the resulting visibility. Foreign projects are 'not found'.")]
        public async Task<ProjectWriteResult> UpdateProject(
            [Description("The project id.")] Guid id,
            [Description("Optional new name (max 100 chars).")] string name = null,
            [Description("Optional new reference (max 100 chars).")] string reference = null,
            [Description("Optional new description (max 5000 chars).")] string description = null,
            [Description("Optional new URL (max 1000 chars).")] string url = null,
            [Description("Optional new status.")] Project.ProjectStatus? status = null,
            [Description("Optional new visibility.")] Project.ProjectViewStatus? viewStatus = null,
            CancellationToken ct = default)
        {
            ValidateProjectFields(name, reference, description, url, status, viewStatus);
            return await projectService.UpdateProjectForMcp(CurrentUserId, id, name, reference, description, url, status, viewStatus, ct);
        }

        private static void ValidateProjectFields(
            string name, string reference, string description, string url,
            Project.ProjectStatus? status, Project.ProjectViewStatus? viewStatus)
        {
            McpWriteValidation.RequireMaxLength(name, 100, "name");
            McpWriteValidation.RequireMaxLength(reference, 100, "reference");
            McpWriteValidation.RequireMaxLength(description, 5000, "description");
            McpWriteValidation.RequireMaxLength(url, 1000, "url");
            if (status.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(status.Value, "status");
            }
            if (viewStatus.HasValue)
            {
                McpWriteValidation.RequireDefinedEnum(viewStatus.Value, "viewStatus");
            }
        }

        /// <summary>Upper bound on a feedback note, matching the Feedback.Note column.</summary>
        private const int MaxFeedbackNoteLength = 5000;

        [McpServerTool(Name = "create_feedback", Title = "Send Feedback", Idempotent = true, Destructive = false, ReadOnly = false, OpenWorld = false),
         Description(
            "Send feedback about 3D Print Log to its maintainers on the user's behalf — a question, " +
            "a bug report, a suggestion, or anything else. Use this only when the user actually asks " +
            "to send feedback; write the note in the user's own words rather than your summary of " +
            "them. The note is required (max 5000 chars) and the feedback is submitted under the " +
            "user's account. 'idempotencyKey' is REQUIRED: submitting feedback emails the " +
            "maintainers, and neither the message nor the email can be taken back, so a retry MUST " +
            "reuse the same key. Same key + same arguments returns the original feedback " +
            "(wasReplayed = true) and sends nothing further; the same key with DIFFERENT arguments " +
            "is a conflict. Feedback cannot be listed, edited, or deleted afterwards.")]
        public async Task<CreateFeedbackResult> CreateFeedback(
            [Description("The kind of feedback: Question, Bug, Suggestion, or Other.")] Feedback.FeedbackType type,
            [Description("The feedback itself, in the user's own words (max 5000 chars).")] string note,
            [Description("Stable key making a retry safe. Required — reuse it verbatim when retrying.")] string idempotencyKey,
            CancellationToken ct = default)
        {
            // Nothing lists the feedback types, so name them rather than reporting a bare
            // "not a valid value" an agent cannot act on. The set is a fixed enum, not per-user.
            if (!Enum.IsDefined(type))
            {
                throw McpToolException.InvalidArguments(
                    $"type is not a valid value. Valid types: {string.Join(", ", Enum.GetNames<Feedback.FeedbackType>())}.");
            }
            if (string.IsNullOrWhiteSpace(note))
            {
                throw McpToolException.InvalidArguments("note is required.");
            }
            McpWriteValidation.RequireMaxLength(note.Trim(), MaxFeedbackNoteLength, "note");

            return await feedbackService.CreateFeedbackForMcp(CurrentUserId, type, note, idempotencyKey, ct);
        }
    }
}
