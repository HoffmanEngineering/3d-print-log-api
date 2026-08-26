using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Enums;
using PrintLogApi.Exceptions;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;
using static PrintLogApi.Services.MeasurementUtilities;

namespace PrintLogApi.Services;

public class FilamentService(
    PrintLogContext context,
    IMapper mapper,
    TelemetryClient telemetry,
    ICacheVersionService cacheVersionService,
    IBlobStorageService blobStorage,
    ILogger<FilamentService> logger) : IFilamentService
{
    private static readonly TimeSpan SasBucket = TimeSpan.FromHours(6);
    private static readonly TimeSpan SasCacheMaxAge = TimeSpan.FromHours(5);

    /// <summary>
    /// Signs thumbnail URLs for a materialized page of summaries.
    /// Signing cannot live in AutoMapper: the list is built inside a ProjectTo
    /// expression paginated in SQL, and SAS generation is async. Keeping it here also
    /// keeps signed URLs out of the anonymous print responses that reuse
    /// FilamentSummaryDto. See the design doc, section 6.
    /// </summary>
    public async Task HydrateImageUrlsAsync(
        IList<FilamentSummaryDto> summaries, CancellationToken ct = default)
    {
        if (summaries.Count == 0) return;

        var ids = summaries.Select(s => s.Id).ToList();

        // One query for the whole page, never one per row.
        var defaults = await context.FilamentImages
            .AsNoTracking()
            .Where(fi => ids.Contains(fi.FilamentId) && fi.IsDefault)
            .Select(fi => new
            {
                fi.FilamentId,
                fi.ContentType,
                OriginalPath = fi.File.Path,
                ThumbnailPath = fi.ThumbnailFile != null ? fi.ThumbnailFile.Path : null
            })
            .ToListAsync(ct);

        var byFilament = defaults.ToDictionary(d => d.FilamentId);

        foreach (var summary in summaries)
        {
            if (!byFilament.TryGetValue(summary.Id, out var image)) continue;

            var path = image.ThumbnailPath ?? image.OriginalPath;
            if (path is null) continue;

            var contentType = image.ThumbnailPath is not null ? "image/webp" : image.ContentType;

            try
            {
                summary.DefaultImageThumbnailUrl = (await blobStorage.GenerateSasInlineUrlAsync(
                    BlobContainers.FilamentImages, Path.GetFileName(path),
                    contentType, SasBucket, SasCacheMaxAge)).ToString();
            }
            catch (Exception ex)
            {
                // The DTO documents this field as nullable on signing failure. One bad
                // blob must not fail the user's entire material list.
                logger.LogWarning(ex, "Failed to sign filament image URL for {FilamentId}", summary.Id);
            }
        }
    }

    /// <summary>
    /// Signs the full image set for one filament's detail response. Same reasoning as
    /// <see cref="HydrateImageUrlsAsync"/>: explicit and post-materialization, never a
    /// member mapping.
    /// </summary>
    public async Task HydrateDetailImageUrlsAsync(
        FilamentDetailDto detail, CancellationToken ct = default)
    {
        var images = await context.FilamentImages
            .AsNoTracking()
            .Where(fi => fi.FilamentId == detail.Id)
            // (DisplayOrder, Id): two concurrent uploads may share a DisplayOrder, so
            // ordering on it alone is not deterministic.
            .OrderBy(fi => fi.DisplayOrder).ThenBy(fi => fi.Id)
            .Select(fi => new
            {
                fi.Id,
                fi.IsDefault,
                fi.DisplayOrder,
                fi.ContentType,
                OriginalPath = fi.File.Path,
                ThumbnailPath = fi.ThumbnailFile != null ? fi.ThumbnailFile.Path : null
            })
            .ToListAsync(ct);

        var hydrated = new List<FilamentImageDto>(images.Count);

        foreach (var image in images)
        {
            var dto = new FilamentImageDto
            {
                Id = image.Id,
                IsDefault = image.IsDefault,
                DisplayOrder = image.DisplayOrder
            };

            dto.Url = await SignOrNullAsync(image.OriginalPath, image.ContentType, detail.Id, ct);

            // Falls back to the original when thumbnail generation failed at upload time.
            dto.ThumbnailUrl = image.ThumbnailPath is null
                ? dto.Url
                : await SignOrNullAsync(image.ThumbnailPath, "image/webp", detail.Id, ct);

            hydrated.Add(dto);
        }

        detail.Images = hydrated;
    }

    private async Task<string?> SignOrNullAsync(
        string? path, string contentType, Guid filamentId, CancellationToken ct)
    {
        if (path is null) return null;

        try
        {
            return (await blobStorage.GenerateSasInlineUrlAsync(
                BlobContainers.FilamentImages, Path.GetFileName(path),
                contentType, SasBucket, SasCacheMaxAge)).ToString();
        }
        catch (Exception ex)
        {
            // Each URL is signed independently so one unsignable blob costs one image,
            // not the whole detail response.
            logger.LogWarning(ex, "Failed to sign filament image URL for {FilamentId}", filamentId);
            return null;
        }
    }

    private IQueryable<FilamentSummaryDto> OwnedInventoryForMcp(
        long userId, string? material, string? color, bool includeInactive)
    {
        var query = context.Filaments.AsNoTracking().Where(f => f.CreatedById == userId);

        if (!includeInactive)
        {
            query = query.Where(f => f.IsActive);
        }
        // Word-boundary matching, NOT exact: users write the same material as "PLA", "PLA
        // (Polylactic Acid)" or "PLA+", and colors as "Light Blue" rather than "Blue". Exact
        // matching missed roughly a third of real inventory.
        //
        // Gate on null (omitted), NOT on whitespace. An explicitly supplied but empty filter is
        // rejected by RequireFilter rather than silently ignored: treating "" or "   " as "no
        // filter" hands back the ENTIRE inventory to a caller who believes it filtered.
        if (material is not null)
        {
            query = query.Where(McpTextMatch.MaterialMatches(material));
        }
        if (color is not null)
        {
            query = query.Where(McpTextMatch.ColorMatches(color));
        }

        return query.ProjectTo<FilamentSummaryDto>(mapper.ConfigurationProvider);
    }

    public async Task<McpPage<MaterialInventoryItem>> GetMaterialInventoryForMcp(
        long userId, int page, int pageSize, string? material, string? color,
        bool includeInactive, CancellationToken ct)
    {
        var projected = OwnedInventoryForMcp(userId, material, color, includeInactive);

        var totalCount = await projected.CountAsync(ct);

        var rows = await projected
            .OrderBy(f => f.DisplayName).ThenBy(f => f.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new
            {
                f.Id,
                f.DisplayName,
                f.Brand,
                f.MaterialType,
                f.ColorName,
                f.FilamentRemaining,
                f.IsActive,
                f.StorageLocation,
                f.DiameterMm,
            })
            .ToListAsync(ct);

        // Negative RemainingGrams are reported as-is: they indicate a real data problem
        // (usage logged beyond the spool's initial weight) that the user should be able to see
        // and fix. Only find_material clamps them, and only when summing availability.
        var items = rows.Select(r => new MaterialInventoryItem(
            r.Id, r.DisplayName!, r.Brand, r.MaterialType!, r.ColorName,
            McpUnits.MgToGrams(r.FilamentRemaining), r.IsActive,
            r.StorageLocation, r.DiameterMm)).ToList();

        var totalPages = pageSize > 0 ? (int)System.Math.Ceiling(totalCount / (double)pageSize) : 0;
        return new McpPage<MaterialInventoryItem>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<double> GetRemainingGramsForMcp(long userId, Guid materialId, CancellationToken ct)
    {
        var remainingMg = await context.Filaments.AsNoTracking()
            .Where(f => f.CreatedById == userId && f.Id == materialId)
            .ProjectTo<FilamentSummaryDto>(mapper.ConfigurationProvider)
            .Select(f => f.FilamentRemaining)
            .FirstOrDefaultAsync(ct);
        return McpUnits.MgToGrams(remainingMg);
    }

    public async Task<MaterialDetail> GetOwnMaterialDetailForMcp(long userId, Guid materialId, CancellationToken ct)
    {
        var material = await context.Filaments.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == materialId && f.CreatedById == userId, ct);
        if (material == null)
        {
            throw McpToolException.NotFound("Material not found.");
        }

        var remaining = await GetRemainingGramsForMcp(userId, materialId, ct);
        return ToMaterialDetail(material, remaining);
    }

    /// <summary>
    /// Projects an owned material to the MCP detail shape. The caller has ALREADY established
    /// ownership; this method does not re-check it and must never be handed a foreign row.
    /// </summary>
    private static MaterialDetail ToMaterialDetail(Filament f, double remainingGrams)
    {
        // The authoritative amount, in the unit the user actually entered. Storage keeps length in
        // METERS while the MCP boundary is mm.
        double? initialInSourceUnit = f.Source switch
        {
            Filament.SourceMeasurement.Length => f.InitialNominalLengthM * 1000.0,
            Filament.SourceMeasurement.Volume => f.InitialNominalVolumeMl,
            _ => f.InitialNominalWeightMg.HasValue ? McpUnits.MgToGrams(f.InitialNominalWeightMg) : null,
        };

        return new MaterialDetail(
            f.Id, f.DisplayName, f.Brand, f.MaterialType, f.MaterialCategoryNickname,
            f.MaterialDensityGramPerCubicCm, f.DiameterMm,
            f.ColorName, f.ColorHex,
            f.Colors ?? new List<string>(),
            f.ColorPattern?.ToString(), f.FinishType?.ToString(),
            (f.Effects ?? new List<FilamentEffect>()).Select(e => e.ToString()).ToList(),
            f.Source.ToString(),
            initialInSourceUnit,
            f.InitialNominalWeightMg.HasValue ? McpUnits.MgToGrams(f.InitialNominalWeightMg) : null,
            f.InitialTotalWeightMg.HasValue ? McpUnits.MgToGrams(f.InitialTotalWeightMg) : null,
            f.SpoolWeightMg.HasValue ? McpUnits.MgToGrams(f.SpoolWeightMg) : null,
            remainingGrams, f.InitialNominalWeightMg.HasValue,
            f.TempRangeStart, f.TempRangeEnd, f.RecommendedTemp, f.RecommendedBedTemp,
            f.InitialLayerTimeS, f.LayerTimeS, f.MeltingTemperature,
            f.InertGas, f.MaterialRefreshRatio,
            f.IsActive, f.IsFavorite, f.Notes,
            f.PurchaseDate, f.StorageLocation,
            f.PurchaseLocation, f.PurchasePriceValue, f.PurchasePriceCurrency, f.PurchaseNotes);
    }

    /// <summary>
    /// Loads the category by nickname, rejecting an unknown one instead of AddFilament's silent
    /// fallback to "filament" — an agent must never be told its resin was created when the row
    /// says otherwise. Also enforces the category's diameter requirement.
    /// </summary>
    private async Task<MaterialCategory> RequireCategory(string? nickname, double? diameterMm, CancellationToken ct)
    {
        var category = await context.MaterialCategories
            .FirstOrDefaultAsync(c => c.Nickname == nickname, ct);
        if (category == null)
        {
            // Name the valid options: nothing lists material categories, so a bare rejection
            // leaves an agent guessing. They are a small fixed seed shared by every user, so the
            // extra query costs nothing on the happy path and only runs when already failing.
            var known = await context.MaterialCategories
                .Select(c => c.Nickname).OrderBy(n => n).ToListAsync(ct);
            throw McpToolException.InvalidArguments(
                $"Unknown material category '{nickname}'. Valid categories: {string.Join(", ", known)}.");
        }
        if (category.HasDiameter && !diameterMm.HasValue)
        {
            throw McpToolException.InvalidArguments("This material category requires a positive diameterMm.");
        }
        return category;
    }

    /// <summary>
    /// Rejects a capacity whose converted weight cannot be stored, BEFORE anything is persisted.
    /// UpdateFilamentMeasurements derives InitialNominalWeightMg through an unchecked long cast,
    /// so an unguarded Length/Volume amount (or a huge density) would silently store garbage
    /// rather than fail. Mirrors the exact formula the fill will use.
    /// <para>
    /// A Length source with no diameter is rejected here rather than defaulted: the fill's early
    /// return only covers diameter-TRACKING categories (see UpdateFilamentMeasurements), so a
    /// resin with a Length source reaches DiameterMm.Value and throws. Substituting 0 would be
    /// worse — it converts to a 0 mg capacity and looks valid.
    /// </para>
    /// </summary>
    private static void RequireRepresentableCapacity(
        McpMeasurementSource source, double initialAmount, double density, double? diameterMm)
    {
        if (source == McpMeasurementSource.Length && !diameterMm.HasValue)
        {
            throw McpToolException.InvalidArguments(
                "A Length source requires diameterMm, which this material category does not track.");
        }

        double mg = source switch
        {
            // The throw above rejects a Length source without a diameter, so this is stated as
            // a throwing fallback rather than a `when` clause: a `when` would fall through to
            // the Weight arm and treat millimetres as grams, which is worse than failing.
            McpMeasurementSource.Length =>
                GetAmountMgFromLengthUnrounded(
                    initialAmount / 1000.0,
                    diameterMm ?? throw new InvalidOperationException(
                        "A Length source reached conversion without a diameter; the guard above should have rejected it."),
                    density),
            McpMeasurementSource.Volume =>
                GetAmountMgFromVolumeUnrounded(initialAmount, density),
            _ => initialAmount * 1000.0,
        };
        // minMg: 1 — a capacity rounding to 0 mg is not "empty", it is a material claiming a
        // tracked capacity of nothing, which every later remaining calculation then believes.
        McpMaterialConversion.RequireMgInRange(mg, "initialAmount", minMg: 1);
    }

    /// <summary>
    /// The single-color/multi-color rule, resolved to the pair the entity actually stores.
    /// An explicit (even empty) 'colors' wins over 'colorHex'; a null 'colors' falls back to the
    /// single hex; an empty 'colors' means NO color, which must survive AddFilament's
    /// empty-means-absent backfill — hence resolving both fields together, never one of them.
    /// </summary>
    /// <remarks>
    /// The per-element null-forgive is load-bearing and NOT accurate: RequireHex only validates
    /// non-null entries, so a caller sending <c>["ff0000", null]</c> persists a null inside
    /// Filament.Colors today. Rejecting or dropping those elements is a runtime behaviour
    /// change, so it is tracked in #57 rather than smuggled in with an annotation.
    /// </remarks>
    private static List<string> ResolveColors(MaterialAttributesInput input) =>
        input.Colors != null
            ? input.Colors.Select(c => c!).ToList()
            : (input.ColorHex != null ? new List<string> { input.ColorHex } : new List<string>());

    private static string? ResolveColorHex(MaterialAttributesInput input)
    {
        var colors = ResolveColors(input);
        return colors.Count > 0 ? colors[0] : null;
    }

    public async Task<CreateMaterialResult> CreateMaterialForMcp(
        long userId, MaterialAttributesInput input, string? idempotencyKey, CancellationToken ct)
    {
        const string toolName = "create_material";

        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            throw McpToolException.InvalidArguments("displayName is required.");
        }
        if (!input.Source.HasValue || !input.InitialAmount.HasValue)
        {
            throw McpToolException.InvalidArguments("source and initialAmount are required.");
        }
        if (!input.DensityGramPerCubicCm.HasValue)
        {
            throw McpToolException.InvalidArguments("densityGramPerCubicCm is required.");
        }

        // Captured here, before the Canonicalize() reassignment below resets what the compiler
        // knows about `input`. Safe to read pre-canonicalisation: Canonicalize only trims string
        // properties, so these three value types pass through its `with` untouched.
        var source = input.Source.Value;
        var density = input.DensityGramPerCubicCm.Value;
        var initialAmount = input.InitialAmount.Value;

        // Canonicalize ONCE, before both hashing and persistence. Anything the fingerprint
        // normalizes away must also be normalized in what we store, or the hash asserts an
        // equivalence the database contradicts.
        input = input.Canonicalize();
        McpMaterialValidation.ValidateAttributes(input);

        if (idempotencyKey != null)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw McpToolException.InvalidArguments("idempotencyKey cannot be blank.");
            }
            // Trim BEFORE the length check: the trimmed value is what gets stored and compared, so
            // that is the value the limit applies to.
            idempotencyKey = idempotencyKey.Trim();
            McpWriteValidation.RequireMaxLength(idempotencyKey, 200, "idempotencyKey");
        }

        string? fingerprint = null;
        if (idempotencyKey != null)
        {
            fingerprint = McpRequestFingerprint.ComputeCreateMaterial(input);
            var replay = await FindIdempotentMaterial(userId, toolName, idempotencyKey, fingerprint, ct);
            if (replay != null)
            {
                return replay;
            }
        }

        var category = await RequireCategory(input.MaterialCategoryNickname, input.DiameterMm, ct);
        RequireRepresentableCapacity(source, initialAmount, density, input.DiameterMm);

        var dto = new AddFilamentDto
        {
            DisplayName = input.DisplayName,
            Brand = input.Brand,
            MaterialType = input.MaterialType,
            MaterialCategoryNickname = category.Nickname,
            MaterialDensityGramPerCubicCm = density,
            DiameterMm = input.DiameterMm,
            ColorName = input.ColorName,
            // Colors is authoritative. Resolve BOTH fields here rather than handing AddFilament a
            // disagreeing pair: it treats a null OR EMPTY Colors as "absent" and rebuilds it from
            // ColorHex, so passing { ColorHex = "1188FF", Colors = [] } would resurrect the color
            // instead of clearing it.
            ColorHex = ResolveColorHex(input),
            Colors = ResolveColors(input),
            ColorPattern = input.ColorPattern,
            FinishType = input.FinishType,
            Effects = (input.Effects ?? Array.Empty<FilamentEffect>()).Distinct().ToList(),
            Source = (Filament.SourceMeasurement)(int)source,
            IsActive = input.IsActive ?? true,
            IsFavorite = input.IsFavorite ?? false,
            StorageLocation = input.StorageLocation,
            Notes = input.Notes,
            SpoolWeightMg = input.SpoolWeightGrams.HasValue
                ? McpMaterialConversion.GramsToMg(input.SpoolWeightGrams.Value, "spoolWeightGrams")
                : null,
            InitialTotalWeightMg = input.InitialTotalWeightGrams.HasValue
                ? McpMaterialConversion.GramsToMg(input.InitialTotalWeightGrams.Value, "initialTotalWeightGrams")
                : null,
            TempRangeStart = input.TempRangeStartC,
            TempRangeEnd = input.TempRangeEndC,
            RecommendedTemp = input.RecommendedTempC,
            RecommendedBedTemp = input.RecommendedBedTempC,
            InitialLayerTimeS = input.InitialLayerTimeS,
            LayerTimeS = input.LayerTimeS,
            MeltingTemperature = input.MeltingTemperatureC,
            InertGas = input.InertGas,
            MaterialRefreshRatio = input.MaterialRefreshRatio,
            PurchaseDate = input.PurchaseDate,
            PurchaseLocation = input.PurchaseLocation,
            PurchasePriceValue = input.PurchasePriceValue,
            PurchasePriceCurrency = input.PurchasePriceCurrency,
            PurchaseNotes = input.PurchaseNotes,
            FilamentAdjustments = new List<FilamentAdjustmentDto>(),
        };

        switch (source)
        {
            case McpMeasurementSource.Weight:
                dto.InitialNominalWeightMg = McpMaterialConversion.GramsToMg(initialAmount, "initialAmount");
                break;
            case McpMeasurementSource.Length:
                dto.InitialNominalLengthM = initialAmount / 1000.0; // mm -> m
                break;
            case McpMeasurementSource.Volume:
                dto.InitialNominalVolumeMl = initialAmount; // ml
                break;
        }

        Filament created;
        if (idempotencyKey == null)
        {
            created = await AddFilament(dto, userId);
        }
        else
        {
            try
            {
                created = await CreateMaterialWithIdempotencyRecord(dto, userId, idempotencyKey, fingerprint, ct);
            }
            catch (DbUpdateException)
            {
                // Possible unique-index race: another identical call created the material first.
                // The transaction rolled back but the failed Added entities are still tracked;
                // clear them so the recovery query reads only committed state, then replay the
                // winner's result. If there is NO such record the failure was something else
                // entirely (a column overflow, a constraint we don't know about) — rethrow it
                // rather than reporting every write failure as an idempotency problem.
                context.ChangeTracker.Clear();
                var concurrent = await FindIdempotentMaterial(userId, toolName, idempotencyKey, fingerprint, ct);
                if (concurrent != null)
                {
                    return concurrent;
                }
                throw;
            }
        }

        cacheVersionService.InvalidateUserCache(userId);
        var remaining = await GetRemainingGramsForMcp(userId, created.Id, ct);
        return new CreateMaterialResult(ToMaterialDetail(created, remaining), WasReplayed: false);
    }

    /// <summary>
    /// Creates the material and its idempotency record atomically. Lets DbUpdateException escape:
    /// only the caller can tell a lost unique-index race (replayable) from a genuine write
    /// failure (not), because only it knows the key and fingerprint to look the winner up with.
    /// </summary>
    private async Task<Filament> CreateMaterialWithIdempotencyRecord(
        AddFilamentDto dto, long userId, string key, string? fingerprint, CancellationToken ct)
    {
        // SqlServerRetryingExecutionStrategy forbids user-initiated transactions unless they
        // run inside an execution strategy, so the whole tx is the retriable unit.
        var strategy = context.Database.CreateExecutionStrategy();
        Filament? created = null;
        await strategy.ExecuteAsync(async () =>
        {
            using var tx = await context.Database.BeginTransactionAsync(ct);
            created = await AddFilament(dto, userId);

            context.McpIdempotencyRecords.Add(
                McpIdempotencyRecordFactory.ForMaterial(userId, key, fingerprint, created.Id));
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        // Null-forgiven: ExecuteAsync runs the delegate synchronously with respect to this
        // method, and AddFilament either assigns or throws.
        return created!;
    }

    private async Task<CreateMaterialResult?> FindIdempotentMaterial(
        long userId, string toolName, string key, string? fingerprint, CancellationToken ct)
    {
        var record = await context.McpIdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.ToolName == toolName && r.IdempotencyKey == key, ct);
        if (record == null)
        {
            return null;
        }

        // A key reused with a DIFFERENT payload is a caller bug, not a retry: replaying would
        // silently discard the new arguments. A null fingerprint is a legacy record with no
        // stored payload to compare, so it replays unconditionally.
        if (record.RequestFingerprint != null && record.RequestFingerprint != fingerprint)
        {
            throw McpToolException.Conflict("This idempotency key was already used with different arguments.");
        }

        var materialId = record.CreatedFilamentId;
        var material = materialId.HasValue
            ? await context.Filaments.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == materialId.Value && f.CreatedById == userId, ct)
            : null;
        if (material == null)
        {
            throw McpToolException.NotFound("The prior result for this idempotency key no longer exists.");
        }

        var remaining = await GetRemainingGramsForMcp(userId, material.Id, ct);
        return new CreateMaterialResult(ToMaterialDetail(material, remaining), WasReplayed: true);
    }

    /// <summary>
    /// Validates the FINAL patched state — everything that depends on fields the caller may not
    /// have touched. Runs after the patches and before SaveChanges.
    /// <para>
    /// This exists because per-request validation is not enough on an update: a density-only edit
    /// supplies no amount yet still recomputes the derived weight (and could overflow), and a
    /// tempRangeStartC-only edit is compared against a stored end the request never mentions.
    /// Checking the request fragment alone would let both through.
    /// </para>
    /// </summary>
    private static void RequireValidFinalState(Filament f)
    {
        if (f.TempRangeStart.HasValue && f.TempRangeEnd.HasValue && f.TempRangeStart.Value > f.TempRangeEnd.Value)
        {
            throw McpToolException.InvalidArguments("tempRangeStartC must not be greater than tempRangeEndC.");
        }

        // The fill's early return only covers diameter-TRACKING categories, so a Length-source
        // material without a diameter reaches DiameterMm.Value and throws.
        if (f.Source == Filament.SourceMeasurement.Length && !f.DiameterMm.HasValue)
        {
            throw McpToolException.InvalidArguments(
                "A Length source requires diameterMm, which this material category does not track.");
        }

        double? mg = f.Source switch
        {
            Filament.SourceMeasurement.Length when f.InitialNominalLengthM.HasValue && f.DiameterMm.HasValue =>
                GetAmountMgFromLengthUnrounded(f.InitialNominalLengthM.Value, f.DiameterMm.Value, f.MaterialDensityGramPerCubicCm),
            Filament.SourceMeasurement.Volume when f.InitialNominalVolumeMl.HasValue =>
                GetAmountMgFromVolumeUnrounded(f.InitialNominalVolumeMl.Value, f.MaterialDensityGramPerCubicCm),
            _ => null,
        };
        if (mg.HasValue)
        {
            McpMaterialConversion.RequireMgInRange(mg.Value, "initialAmount", minMg: 1);
        }
    }

    public async Task<MaterialDetail> UpdateOwnMaterialForMcp(
        long userId, Guid materialId, MaterialAttributesInput input, ISet<string> clear, CancellationToken ct)
    {
        clear ??= new HashSet<string>();

        // Canonicalize ONCE, before validation and persistence — same rule as create.
        input = input.Canonicalize();
        McpMaterialValidation.ValidateAttributes(input);
        // Enforced HERE, not only in the tool wrapper: the service is the boundary every caller
        // goes through, so this is where "displayName is not clearable" is actually true.
        McpMaterialValidation.RequireClearableFields(clear);
        RequireNoSetAndClearCollision(input, clear);

        if (input.Source.HasValue != input.InitialAmount.HasValue)
        {
            throw McpToolException.InvalidArguments("source and initialAmount must be provided together.");
        }

        var material = await context.Filaments
            .Include(f => f.MaterialCategory)
            .FirstOrDefaultAsync(f => f.Id == materialId && f.CreatedById == userId, ct);
        if (material == null)
        {
            throw McpToolException.NotFound("Material not found.");
        }

        try
        {
            await ApplyMaterialPatch(material, input, clear, userId, ct);
        }
        catch (McpToolException)
        {
            // The patch mutates the TRACKED entity, so a rejection partway through leaves dirty
            // state that a later SaveChangesAsync on this same context would happily commit.
            // Nothing else saves within an MCP request today — but "rejected edits change
            // nothing" should be a property of the code, not of the current call graph.
            context.ChangeTracker.Clear();
            throw;
        }

        cacheVersionService.InvalidateUserCache(userId);

        var remaining = await GetRemainingGramsForMcp(userId, material.Id, ct);
        return ToMaterialDetail(material, remaining);
    }

    /// <summary>
    /// Applies the patch to a tracked, already-ownership-checked entity and saves it. Throws
    /// before SaveChanges on any rejection; the caller discards the dirty tracked state.
    /// </summary>
    private async Task ApplyMaterialPatch(
        Filament material, MaterialAttributesInput input, ISet<string> clear, long userId, CancellationToken ct)
    {
        // --- Non-clearable identity/computation fields: set only. ---
        if (input.DisplayName != null)
        {
            if (input.DisplayName.Length == 0)
            {
                throw McpToolException.InvalidArguments("displayName cannot be empty.");
            }
            material.DisplayName = input.DisplayName;
        }
        if (input.MaterialType != null)
        {
            material.MaterialType = input.MaterialType;
        }
        if (input.DensityGramPerCubicCm.HasValue)
        {
            material.MaterialDensityGramPerCubicCm = input.DensityGramPerCubicCm.Value;
        }
        if (input.IsActive.HasValue)
        {
            material.IsActive = input.IsActive.Value;
        }
        if (input.IsFavorite.HasValue)
        {
            material.IsFavorite = input.IsFavorite.Value;
        }

        // --- Diameter, then the category, so the requirement is checked against the FINAL pair. ---
        if (clear.Contains("diameterMm"))
        {
            material.DiameterMm = null;
        }
        else if (input.DiameterMm.HasValue)
        {
            material.DiameterMm = input.DiameterMm.Value;
        }

        if (input.MaterialCategoryNickname != null)
        {
            var category = await RequireCategory(input.MaterialCategoryNickname, material.DiameterMm, ct);
            material.MaterialCategoryNickname = category.Nickname;
            material.MaterialCategory = category;
        }
        else if (material.MaterialCategory.HasDiameter && !material.DiameterMm.HasValue)
        {
            // Clearing the diameter of a diameter-tracking material would silently disable every
            // later length conversion on it.
            throw McpToolException.InvalidArguments("This material category requires a positive diameterMm.");
        }

        // --- Colors. Colors and ColorHex are one field with two faces; they move together. ---
        if (clear.Contains("colorHex") || clear.Contains("colors"))
        {
            material.ColorHex = null;
            material.Colors = new List<string>();
        }
        else if (input.Colors != null)
        {
            // Null elements survive validation here too — see ResolveColors, tracked in #57.
            material.Colors = input.Colors.Select(c => c!).ToList();
            material.ColorHex = material.Colors.Count > 0 ? material.Colors[0] : null;
        }
        else if (input.ColorHex != null)
        {
            material.ColorHex = input.ColorHex;
            material.Colors = new List<string> { input.ColorHex };
        }

        PatchString(clear, "colorName", input.ColorName, v => material.ColorName = v);
        PatchString(clear, "brand", input.Brand, v => material.Brand = v);
        PatchString(clear, "storageLocation", input.StorageLocation, v => material.StorageLocation = v);
        PatchString(clear, "notes", input.Notes, v => material.Notes = v);
        PatchString(clear, "inertGas", input.InertGas, v => material.InertGas = v);
        PatchString(clear, "purchaseLocation", input.PurchaseLocation, v => material.PurchaseLocation = v);
        PatchString(clear, "purchasePriceValue", input.PurchasePriceValue, v => material.PurchasePriceValue = v);
        PatchString(clear, "purchasePriceCurrency", input.PurchasePriceCurrency, v => material.PurchasePriceCurrency = v);
        PatchString(clear, "purchaseNotes", input.PurchaseNotes, v => material.PurchaseNotes = v);

        PatchValue(clear, "colorPattern", input.ColorPattern, v => material.ColorPattern = v);
        PatchValue(clear, "finishType", input.FinishType, v => material.FinishType = v);
        PatchValue(clear, "purchaseDate", input.PurchaseDate, v => material.PurchaseDate = v);
        PatchValue(clear, "tempRangeStartC", input.TempRangeStartC, v => material.TempRangeStart = v);
        PatchValue(clear, "tempRangeEndC", input.TempRangeEndC, v => material.TempRangeEnd = v);
        PatchValue(clear, "recommendedTempC", input.RecommendedTempC, v => material.RecommendedTemp = v);
        PatchValue(clear, "recommendedBedTempC", input.RecommendedBedTempC, v => material.RecommendedBedTemp = v);
        PatchValue(clear, "initialLayerTimeS", input.InitialLayerTimeS, v => material.InitialLayerTimeS = v);
        PatchValue(clear, "layerTimeS", input.LayerTimeS, v => material.LayerTimeS = v);
        PatchValue(clear, "meltingTemperatureC", input.MeltingTemperatureC, v => material.MeltingTemperature = v);
        PatchValue(clear, "materialRefreshRatio", input.MaterialRefreshRatio, v => material.MaterialRefreshRatio = v);

        if (clear.Contains("effects"))
        {
            material.Effects = new List<FilamentEffect>();
        }
        else if (input.Effects != null)
        {
            material.Effects = input.Effects.Distinct().ToList();
        }

        if (clear.Contains("spoolWeightGrams"))
        {
            material.SpoolWeightMg = null;
        }
        else if (input.SpoolWeightGrams.HasValue)
        {
            material.SpoolWeightMg = McpMaterialConversion.GramsToMg(input.SpoolWeightGrams.Value, "spoolWeightGrams");
        }

        if (clear.Contains("initialTotalWeightGrams"))
        {
            material.InitialTotalWeightMg = null;
        }
        else if (input.InitialTotalWeightGrams.HasValue)
        {
            material.InitialTotalWeightMg = McpMaterialConversion.GramsToMg(input.InitialTotalWeightGrams.Value, "initialTotalWeightGrams");
        }

        // --- Capacity. The source names the authoritative field; the fill derives the rest. ---
        // The && is not a weakening: the mismatch check earlier in this method has already
        // rejected a request carrying one of the pair without the other, so Source non-null
        // implies InitialAmount non-null here.
        if (input.Source is { } source && input.InitialAmount is { } initialAmount)
        {
            material.Source = (Filament.SourceMeasurement)(int)source;
            switch (source)
            {
                case McpMeasurementSource.Weight:
                    material.InitialNominalWeightMg = McpMaterialConversion.GramsToMg(initialAmount, "initialAmount");
                    break;
                case McpMeasurementSource.Length:
                    material.InitialNominalLengthM = initialAmount / 1000.0; // mm -> m
                    break;
                case McpMeasurementSource.Volume:
                    material.InitialNominalVolumeMl = initialAmount; // ml
                    break;
            }
        }

        // Validate the FINAL merged state before touching the database, so a rejected edit leaves
        // the material exactly as it was.
        RequireValidFinalState(material);

        // Recompute weight and the other derived fields from the authoritative field and the
        // current density/diameter. This is the website's behavior, mirrored deliberately.
        UpdateFilamentMeasurements(material);

        material.UpdatedById = userId;
        await context.SaveChangesAsync(ct);
    }

    private static void PatchString(ISet<string> clear, string field, string? value, Action<string?> set)
    {
        if (clear.Contains(field))
        {
            set(null);
        }
        else if (value != null)
        {
            set(value);
        }
    }

    private static void PatchValue<T>(ISet<string> clear, string field, T? value, Action<T?> set) where T : struct
    {
        if (clear.Contains(field))
        {
            set(null);
        }
        else if (value.HasValue)
        {
            set(value);
        }
    }

    /// <summary>
    /// Setting and clearing the same field in one call is contradictory, so it is rejected rather
    /// than resolved by precedence — either resolution would silently do the opposite of half the
    /// request.
    /// </summary>
    private static void RequireNoSetAndClearCollision(MaterialAttributesInput input, ISet<string> clear)
    {
        void Check(string field, bool provided)
        {
            if (provided && clear.Contains(field))
            {
                throw McpToolException.InvalidArguments($"'{field}' cannot be both set and cleared.");
            }
        }

        Check("brand", input.Brand != null);
        Check("colorName", input.ColorName != null);
        Check("colorHex", input.ColorHex != null);
        Check("colors", input.Colors != null);
        // Colors and ColorHex clear jointly, so naming EITHER while setting the OTHER is the same
        // contradiction.
        Check("colors", input.ColorHex != null);
        Check("colorHex", input.Colors != null);
        Check("storageLocation", input.StorageLocation != null);
        Check("notes", input.Notes != null);
        Check("inertGas", input.InertGas != null);
        Check("purchaseLocation", input.PurchaseLocation != null);
        Check("purchasePriceValue", input.PurchasePriceValue != null);
        Check("purchasePriceCurrency", input.PurchasePriceCurrency != null);
        Check("purchaseNotes", input.PurchaseNotes != null);
        Check("purchaseDate", input.PurchaseDate.HasValue);
        Check("spoolWeightGrams", input.SpoolWeightGrams.HasValue);
        Check("initialTotalWeightGrams", input.InitialTotalWeightGrams.HasValue);
        Check("diameterMm", input.DiameterMm.HasValue);
        Check("tempRangeStartC", input.TempRangeStartC.HasValue);
        Check("tempRangeEndC", input.TempRangeEndC.HasValue);
        Check("recommendedTempC", input.RecommendedTempC.HasValue);
        Check("recommendedBedTempC", input.RecommendedBedTempC.HasValue);
        Check("initialLayerTimeS", input.InitialLayerTimeS.HasValue);
        Check("layerTimeS", input.LayerTimeS.HasValue);
        Check("meltingTemperatureC", input.MeltingTemperatureC.HasValue);
        Check("materialRefreshRatio", input.MaterialRefreshRatio.HasValue);
        Check("colorPattern", input.ColorPattern.HasValue);
        Check("finishType", input.FinishType.HasValue);
        Check("effects", input.Effects != null);
    }

    public async Task<MaterialWriteResult> AdjustMaterialRemainingForMcp(
        long userId, Guid materialId, McpMeasurementSource source, double delta, string? notes,
        CancellationToken ct)
    {
        McpWriteValidation.RequireFiniteAmount(delta);
        if (delta == 0)
        {
            throw McpToolException.InvalidArguments("delta must be non-zero.");
        }

        var material = await context.Filaments
            .Include(f => f.MaterialCategory)
            .FirstOrDefaultAsync(f => f.Id == materialId && f.CreatedById == userId, ct);
        if (material == null)
        {
            throw McpToolException.NotFound("Material not found.");
        }

        // Record the adjustment in the caller's source unit; the existing helper derives the rest,
        // including AmountMg — the weight the remaining calculation actually sums.
        var adjustment = new FilamentAdjustment
        {
            FilamentId = materialId,
            Source = (FilamentAdjustment.SourceMeasurement)(int)source,
            Notes = notes,
            CreatedById = userId,
            UpdatedById = userId,
        };
        switch (source)
        {
            case McpMeasurementSource.Weight:
                adjustment.AmountMg = checked((long)Math.Round(delta * 1000.0)); // g -> mg
                break;
            case McpMeasurementSource.Length:
                adjustment.LengthInM = delta / 1000.0; // mm -> m
                break;
            case McpMeasurementSource.Volume:
                adjustment.VolumeMl = delta; // ml
                break;
        }
        UpdateFilamentAdjustmentMeasurements(adjustment, material);

        if (adjustment.AmountMg is null)
        {
            throw McpToolException.InvalidArguments(
                "This material is missing the density/diameter needed to convert the adjustment to a weight.");
        }

        var deltaGrams = McpUnits.MgToGrams(adjustment.AmountMg);
        var beforeGrams = await GetRemainingGramsForMcp(userId, materialId, ct);
        var afterGrams = Math.Round(beforeGrams + deltaGrams, 3, MidpointRounding.AwayFromZero);
        var capacityGrams = McpUnits.MgToGrams(material.InitialNominalWeightMg);

        if (afterGrams < 0)
        {
            throw McpToolException.InvalidArguments("Adjustment would drive remaining below zero.");
        }
        if (capacityGrams > 0 && afterGrams > capacityGrams)
        {
            throw McpToolException.InvalidArguments("Adjustment would exceed the material's original capacity.");
        }

        context.FilamentAdjustments.Add(adjustment);
        await context.SaveChangesAsync(ct);
        cacheVersionService.InvalidateUserCache(userId);

        return new MaterialWriteResult(materialId, beforeGrams, afterGrams);
    }

    public async Task<MaterialInventoryItem> SetMaterialActiveForMcp(long userId, Guid materialId, bool isActive, CancellationToken ct)
    {
        var material = await context.Filaments
            .FirstOrDefaultAsync(f => f.Id == materialId && f.CreatedById == userId, ct);
        if (material == null)
        {
            throw McpToolException.NotFound("Material not found.");
        }

        material.IsActive = isActive;
        material.UpdatedById = userId;
        await context.SaveChangesAsync(ct);
        cacheVersionService.InvalidateUserCache(userId);

        var remaining = await GetRemainingGramsForMcp(userId, material.Id, ct);
        return new MaterialInventoryItem(
            material.Id, material.DisplayName!, material.Brand, material.MaterialType!, material.ColorName,
            remaining, material.IsActive, material.StorageLocation, material.DiameterMm);
    }

    public const int MaxGroups = 20;
    public const int MaxSpoolsPerGroup = 25;

    /// <summary>
    /// Grouping happens in memory (grouping free-text pairs is awkward to page in SQL), so the
    /// candidate set must be bounded in SQL first — otherwise a user with thousands of spools
    /// materializes all of them.
    ///
    /// Sized from production (measured 2026-07-13, 4,022 users): p50 2 spools, p95 27, p99 78,
    /// max 571. A 500 cap truncated the largest real account; 1000 clears it with headroom while
    /// still bounding what a single call can materialize. Truncation past this is not a wrong
    /// answer — it surfaces as CandidatesTruncated and an indeterminate sufficiency result — so
    /// this cap is a quality bound, not a correctness one.
    /// </summary>
    public const int MaxCandidates = 1000;

    public async Task<FindMaterialResult> FindMaterialForMcp(
        long userId, string? material, string? color, double? requiredGrams, CancellationToken ct)
    {
        var projected = OwnedInventoryForMcp(userId, material, color, includeInactive: false);

        // Order by remaining weight so that if the candidate set is truncated, the spools most
        // likely to satisfy the requirement are the ones that survive.
        var candidates = await projected
            .OrderByDescending(f => f.FilamentRemaining ?? 0)
            .ThenBy(f => f.Id)
            .Take(MaxCandidates + 1) // +1 detects truncation without a second COUNT
            .Select(f => new SpoolItem(
                f.Id, f.DisplayName!, f.Brand, f.MaterialType!, f.ColorName,
                f.DiameterMm, McpUnits.MgToGrams(f.FilamentRemaining), f.StorageLocation))
            .ToListAsync(ct);

        var candidatesTruncated = candidates.Count > MaxCandidates;
        var spools = candidates.Take(MaxCandidates).ToList();

        var groups = spools
            .GroupBy(s => new { s.Material, s.Color })
            .Select(g => BuildGroup(
                g.Key.Material, g.Key.Color, g.ToList(), requiredGrams, candidatesTruncated))
            .ToList();

        // When a requirement is given, rank groups that satisfy it from a SINGLE spool first.
        // Ordering by total grams alone can truncate away the only unattended solution: twenty
        // groups of ten 100 g spools all outrank one group holding a single 600 g spool.
        groups = requiredGrams.HasValue
            ? groups
                .OrderByDescending(g => g.SufficientOnLargestSpool == true)
                .ThenByDescending(g => g.LargestSpoolGrams)
                .ThenByDescending(g => g.TotalGrams)
                .ThenBy(g => g.Material)
                .ToList()
            : groups
                .OrderByDescending(g => g.TotalGrams)
                .ThenBy(g => g.Material)
                .ToList();

        return new FindMaterialResult(
            requiredGrams,
            groups.Take(MaxGroups).ToList(),
            groups.Count > MaxGroups,
            candidatesTruncated);
    }

    private static MaterialGroup BuildGroup(
        string material, string? color, List<SpoolItem> spools, double? requiredGrams,
        bool candidatesTruncated)
    {
        // Largest first: guarantees a spool that alone meets the requirement is never the one
        // dropped by the per-group cap.
        var ordered = spools.OrderByDescending(s => s.RemainingGrams).ThenBy(s => s.Id).ToList();

        // A negative remaining weight is a data error (more logged as used than the spool held).
        // Clamp it to zero when summing so one corrupt spool cannot cancel out good ones and
        // make a printable job look unprintable.
        var total = ordered.Sum(s => Math.Max(0, s.RemainingGrams));
        var largest = ordered.Count > 0 ? Math.Max(0, ordered[0].RemainingGrams) : 0;

        List<SpoolItem>? combination = null;
        if (requiredGrams is { } required && total >= required)
        {
            // Minimal prefix that reaches the requirement — the evidence behind the claim, so
            // the agent can say "120 g from this spool and 180 g from that one".
            combination = new List<SpoolItem>();
            var running = 0d;
            foreach (var spool in ordered)
            {
                if (running >= required)
                {
                    break;
                }

                combination.Add(spool);
                running += Math.Max(0, spool.RemainingGrams);
            }
        }

        // Truncation drops the SMALLEST spools (candidates are ordered by remaining weight), so:
        //  - largest >= required stays trustworthy: the biggest spools always survive.
        //  - total >= required stays trustworthy when TRUE: a subset proving it was found.
        //  - total >= required is UNKNOWABLE when false: the dropped spools might have closed
        //    the gap. Reporting a confident `false` there is a wrong answer, so report null
        //    (indeterminate) instead and let candidatesTruncated explain why.
        bool? meetsByCombining = null;
        if (requiredGrams is { } needed)
        {
            var reached = total >= needed;
            meetsByCombining = reached || !candidatesTruncated ? reached : null;
        }

        return new MaterialGroup(
            material,
            color,
            ordered.Count,
            total,
            largest,
            ordered.Take(MaxSpoolsPerGroup).ToList(),
            ordered.Count > MaxSpoolsPerGroup,
            requiredGrams.HasValue ? largest >= requiredGrams.Value : null,
            meetsByCombining,
            combination);
    }

    public async Task<PagedList<FilamentSummaryDto>> GetFilamentSummaryForUser(
        long userId,
        SortDirection sortDirection,
        FilamentSummarySortColumn sortColumn,
        int pageNumber,
        int pageSize,
        // All three are optional filters the controller binds from the query string, so null
        // is the normal "not filtering on this" case -- the guards below have always handled
        // it. The annotation records that rather than changing it (#45).
        string? searchText,
        string? filterByMaterialCategoryNickname,
        string? filterByStorageLocation,
        bool? includeInactive,
        bool? showFavoritesOnly,
        bool? showLoadedFilamentOnly,
        List<ColorPatternType>? colorPatterns = null,
        List<FilamentFinishType>? finishTypes = null,
        List<FilamentEffect>? effects = null)
    {
        // PagedRequest.PageSize is an unconstrained int that flows straight into Take().
        // Hydration now signs one URL per row, so an unbounded page is a request-thread
        // cost as well as a response-size one.
        const int MaxPageSize = 100;
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var filament = context.Filaments
            .Include(f => f.MaterialCategory)
            .Where(f => f.CreatedById == userId);

        // Filter out unloaded-filaments if requested.
        if (showLoadedFilamentOnly.HasValue && showLoadedFilamentOnly.Value == true)
        {
            filament = filament.Where(f => f.PrinterFilaments!.Any(pf => !pf.UnloadedDateTime.HasValue));
        }

        if (!string.IsNullOrEmpty(filterByMaterialCategoryNickname))
        {
            filament = filament.Where(f => f.MaterialCategory.Nickname == filterByMaterialCategoryNickname);
        }

        if (!string.IsNullOrEmpty(filterByStorageLocation))
        {
            if (filterByStorageLocation == "__unassigned__")
                filament = filament.Where(f => f.StorageLocation == null || f.StorageLocation == "");
            else
                filament = filament.Where(f => f.StorageLocation == filterByStorageLocation);
        }

        if (colorPatterns is { Count: > 0 })
        {
            filament = filament.Where(f =>
                f.ColorPattern != null && colorPatterns.Contains(f.ColorPattern.Value));
        }

        if (finishTypes is { Count: > 0 })
        {
            filament = filament.Where(f =>
                f.FinishType != null && finishTypes.Contains(f.FinishType.Value));
        }

        if (effects is { Count: > 0 })
        {
            // Any-match: filament has at least one of the requested effects
            filament = filament.Where(f => f.Effects != null && f.Effects.Any(e => effects.Contains(e)));
        }

        var filamentsBase = filament
            .ProjectTo<FilamentSummaryDto>(mapper.ConfigurationProvider)
            .AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            // Split on any spaces and search separately.
            var criterias = searchText.Split('"')
                 .Select((element, index) => index % 2 == 0  // If even index
                                       ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)  // Split the item
                                       : new string[] { element })  // Keep the entire item
                 .SelectMany(element => element).ToList();
            foreach (var text in criterias)
            {
                filamentsBase = filamentsBase.Where(f => f.DisplayName!.Contains(text) || f.Brand!.Contains(text) || f.ColorName!.Contains(text) || f.MaterialType!.Contains(text) || f.Notes!.Contains(text));
            }

        }



        // Filter out inactives unless specified
        if (!includeInactive.HasValue || includeInactive.Value == false)
        {
            filamentsBase = filamentsBase.Where(f => f.IsActive == true);
        }

        // Filter out non-favorites if requested.
        if (showFavoritesOnly.HasValue && showFavoritesOnly.Value == true)
        {
            filamentsBase = filamentsBase.Where(f => f.IsFavorite == true);
        }

        if (sortColumn == FilamentSummarySortColumn.DisplayName)
        {
            if (sortDirection == SortDirection.Asc)
            {
                filamentsBase = filamentsBase.OrderBy(f => PrintLogContext.fnNaturalSort(f.DisplayName!)).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
            }
            else
            {
                filamentsBase = filamentsBase.OrderByDescending(f => PrintLogContext.fnNaturalSort(f.DisplayName!)).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
            }
        }
        else if (sortColumn == FilamentSummarySortColumn.FilamentRemaining)
        {
            if (sortDirection == SortDirection.Asc)
            {
                filamentsBase = filamentsBase.OrderBy(f => f.FilamentRemaining).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
            }
            else
            {
                filamentsBase = filamentsBase.OrderByDescending(f => f.FilamentRemaining).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
            }
        }
        else if (sortColumn == FilamentSummarySortColumn.MaterialType)
        {
            if (sortDirection == SortDirection.Asc)
            {
                filamentsBase = filamentsBase.OrderBy(f => f.MaterialType).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
            }
            else
            {
                filamentsBase = filamentsBase.OrderByDescending(f => f.MaterialType).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
            }

        }
        else if (sortColumn == FilamentSummarySortColumn.Brand)
        {
            if (sortDirection == SortDirection.Asc)
            {
                filamentsBase = filamentsBase.OrderBy(f => f.Brand).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
            }
            else
            {
                filamentsBase = filamentsBase.OrderByDescending(f => f.Brand).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
            }

        }
        else if (sortColumn == FilamentSummarySortColumn.Color)
        {
            if (sortDirection == SortDirection.Asc)
            {
                filamentsBase = filamentsBase.OrderBy(f => f.ColorName).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
            }
            else
            {
                filamentsBase = filamentsBase.OrderByDescending(f => f.ColorName).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
            }

        }
        else if (sortColumn == FilamentSummarySortColumn.StorageLocation)
        {
            if (sortDirection == SortDirection.Asc)
            {
                filamentsBase = filamentsBase.OrderBy(f => f.StorageLocation).ThenBy(f => f.DisplayName).ThenBy(f => f.CreatedDate).ThenBy(f => f.Id);
            }
            else
            {
                filamentsBase = filamentsBase.OrderByDescending(f => f.StorageLocation).ThenByDescending(f => f.DisplayName).ThenByDescending(f => f.CreatedDate).ThenByDescending(f => f.Id);
            }

        }

        var page = await PagedList<FilamentSummaryDto>.CreateAsync(filamentsBase, pageNumber, pageSize);

        // After materialization, never inside the ProjectTo expression.
        await HydrateImageUrlsAsync(page.Items);

        return page;
    }

    public async Task<Filament?> GetFilamentById(Guid id)
    {
        return await context.Filaments
                .Where(f => f.Id == id)
                .Include(f => f.FilamentAdjustments)
                .Include(f => f.PrintFilaments)
                .Include(f => f.MaterialCategory)
                .AsSplitQuery()
                .SingleOrDefaultAsync();
    }

    /// <summary>
    /// Add Filament
    /// </summary>
    /// <param name="filament">The filament to add</param>
    /// <param name="userId">The user adding the filament</param>
    /// <returns></returns>
    public async Task<int> GetMaxImagesPerFilament(long userId)
    {
        var subscription = await context.Subscriptions
            .Where(s => s.UserId == userId)
            .AsNoTracking()
            .SingleOrDefaultAsync();

        return subscription?.Status == SubscriptionStatus.Active
            ? SubscriptionLimits.ProMaxImagesPerFilament
            : SubscriptionLimits.FreeMaxImagesPerFilament;
    }

    public async Task<Filament> AddFilament(AddFilamentDto filament, long userId)
    {
        // Backward-compat: old clients send ColorHex only — normalize to Colors array
        if ((filament.Colors == null || filament.Colors.Count == 0) && !string.IsNullOrWhiteSpace(filament.ColorHex))
        {
            filament.Colors = new List<string> { filament.ColorHex };
            filament.ColorPattern ??= ColorPatternType.Solid;
            filament.FinishType ??= FilamentFinishType.Standard;
        }

        // Always keep ColorHex in sync with Colors[0]
        if (filament.Colors != null && filament.Colors.Count > 0)
        {
            filament.ColorHex = filament.Colors[0];
            filament.ColorPattern ??= ColorPatternType.Solid;
            filament.FinishType ??= FilamentFinishType.Standard;
        }

        var newFilament = mapper.Map<Filament>(filament);

        var materialCategory = await context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == newFilament.MaterialCategoryNickname);

        if (materialCategory == null)
        {
            // Todo, throw error?
            materialCategory = await context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == "filament");
        }

        newFilament.MaterialCategory = materialCategory!;

        UpdateFilamentMeasurements(newFilament);

        foreach (var adjustment in newFilament.FilamentAdjustments!)
        {
            adjustment.CreatedById = userId;
            adjustment.UpdatedById = userId;

            UpdateFilamentAdjustmentMeasurements(adjustment, newFilament);
        }

        newFilament.CreatedById = userId;
        newFilament.UpdatedById = userId;

        context.Filaments.Add(newFilament);
        await context.SaveChangesAsync();

        var filamentId = newFilament.Id;

        telemetry.TrackEvent("FilamentAdd");

        // Null-forgiven: the filament was just persisted, so the re-read always finds it.
        return (await GetFilamentById(filamentId))!;
    }

    private void UpdateFilamentAdjustmentMeasurements(FilamentAdjustment adjustment, Filament filament)
    {
        if (filament is null || !(filament.MaterialDensityGramPerCubicCm >= 0) || (filament.MaterialCategory.HasDiameter && (!filament.DiameterMm.HasValue || !(filament.DiameterMm >= 0))))
        {
            // Skip any filament that doesn't have the required properties to compute.
            return;
        }

        if (adjustment.Source == FilamentAdjustment.SourceMeasurement.Length)
        {
            if (adjustment.LengthInM.HasValue)
            {
                // The diameter test is what the Volume and Weight branches below already do.
                // Without it a material whose category tracks no diameter (resin, powder)
                // reaches DiameterMm.Value and throws. The else clears the derived fields
                // rather than leaving a stale value behind, matching those branches.
                if (filament.MaterialCategory.HasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    adjustment.AmountMg = GetAmountMgFromLength(adjustment.LengthInM.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                    adjustment.VolumeMl = GetVolumeInMlFromLengthM(adjustment.LengthInM.Value, diameterMm);
                }
                else
                {
                    adjustment.AmountMg = null;
                    adjustment.VolumeMl = null;
                }
            }
        }
        else if (adjustment.Source == FilamentAdjustment.SourceMeasurement.Volume)
        {
            if (adjustment.VolumeMl.HasValue)
            {
                adjustment.AmountMg = GetAmountMgFromVolume(adjustment.VolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                if (filament.MaterialCategory.HasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    adjustment.LengthInM = GetLengthInMetersFromVolume(adjustment.VolumeMl.Value, diameterMm);
                }
                else
                {
                    adjustment.LengthInM = null;
                }
            }
        }
        else
        {

            if (adjustment.AmountMg.HasValue)
            {
                adjustment.VolumeMl = GetVolumeInMlFromAmount(adjustment.AmountMg.Value, filament.MaterialDensityGramPerCubicCm);

                if (filament.MaterialCategory.HasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    adjustment.LengthInM = GetLengthInMetersFromAmount(adjustment.AmountMg.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                }
                else
                {
                    adjustment.LengthInM = null;
                }
            }
        }
    }

    private void UpdateFilamentMeasurements(Filament filament)
    {

        if (filament is null || !(filament.MaterialDensityGramPerCubicCm >= 0) || (filament.MaterialCategory.HasDiameter && (!filament.DiameterMm.HasValue || !(filament.DiameterMm >= 0))))
        {
            // Skip any filament that doesn't have the required properties to compute.
            return;
        }

        if (filament.Source == Filament.SourceMeasurement.Length)
        {
            if (filament.InitialNominalLengthM.HasValue)
            {
                // The diameter test is what the Volume and Weight branches below already do.
                // Without it a material whose category tracks no diameter (resin, powder)
                // reaches DiameterMm.Value and throws. The else clears the derived fields
                // rather than leaving a stale value behind, matching those branches.
                if (filament.MaterialCategory.HasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    filament.InitialNominalWeightMg = GetAmountMgFromLength(filament.InitialNominalLengthM.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                    filament.InitialNominalVolumeMl = GetVolumeInMlFromLengthM(filament.InitialNominalLengthM.Value, diameterMm);
                }
                else
                {
                    filament.InitialNominalWeightMg = null;
                    filament.InitialNominalVolumeMl = null;
                }
            }
        }
        else if (filament.Source == Filament.SourceMeasurement.Volume)
        {
            if (filament.InitialNominalVolumeMl.HasValue)
            {
                filament.InitialNominalWeightMg = GetAmountMgFromVolume(filament.InitialNominalVolumeMl.Value, filament.MaterialDensityGramPerCubicCm);

                if (filament.MaterialCategory.HasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    filament.InitialNominalLengthM = GetLengthInMetersFromVolume(filament.InitialNominalVolumeMl.Value, diameterMm);
                }
                else
                {
                    filament.InitialNominalLengthM = null;
                }
            }
        }
        else
        {

            if (filament.InitialNominalWeightMg.HasValue)
            {
                filament.InitialNominalVolumeMl = GetVolumeInMlFromAmount(filament.InitialNominalWeightMg.Value, filament.MaterialDensityGramPerCubicCm);

                if (filament.MaterialCategory.HasDiameter && filament.DiameterMm is { } diameterMm)
                {
                    filament.InitialNominalLengthM = GetLengthInMetersFromAmount(filament.InitialNominalWeightMg.Value, diameterMm, filament.MaterialDensityGramPerCubicCm);
                }
                else
                {
                    filament.InitialNominalLengthM = null;
                }
            }
        }
    }

    public async Task<Filament> UpdateFilament(Guid id, FilamentDetailDto dto, long userId)
    {
        // Backward-compat: old clients send ColorHex only — normalize to Colors array
        if ((dto.Colors == null || dto.Colors.Count == 0) && !string.IsNullOrWhiteSpace(dto.ColorHex))
        {
            dto.Colors = new List<string> { dto.ColorHex };
            dto.ColorPattern = ColorPatternType.Solid;
            dto.FinishType = FilamentFinishType.Standard;
        }

        // Always keep ColorHex in sync with Colors[0]
        if (dto.Colors != null && dto.Colors.Count > 0)
        {
            dto.ColorHex = dto.Colors[0];
        }

        var existingFilament = await GetFilamentById(id);

        if (existingFilament == null)
        {
            throw new ArgumentNullException(nameof(id));
        }

        var updatedFilament = mapper.Map<FilamentDetailDto, Filament>(dto, existingFilament);

        var materialCategory = await context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == updatedFilament.MaterialCategoryNickname);

        if (materialCategory == null)
        {
            // Todo, throw error?
            materialCategory = await context.MaterialCategories.FirstOrDefaultAsync(f => f.Nickname == "filament");
        }

        // Null-forgiven: the "filament" fallback lookup above can itself return null if that
        // seeded category is missing, which already threw here. The existing "Todo, throw
        // error?" comment marks the same gap; it is tracked in #57.
        updatedFilament.MaterialCategoryNickname = materialCategory!.Nickname;
        updatedFilament.MaterialCategory = materialCategory;

        UpdateFilamentMeasurements(updatedFilament);

        foreach (var adjustment in updatedFilament.FilamentAdjustments!)
        {
            adjustment.CreatedById = userId;
            adjustment.UpdatedById = userId;

            UpdateFilamentAdjustmentMeasurements(adjustment, updatedFilament);
        }


        // Set UpdatedByIds
        updatedFilament.UpdatedById = userId;

        context.Entry(updatedFilament).State = EntityState.Modified;

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!FilamentExists(id))
            {
                throw new DoesNotExistException();
            }
            else
            {
                throw;
            }
        }

        telemetry.TrackEvent("FilamentEdit");

        return updatedFilament;
    }

    public async Task<string[]> GetFilamentStorageLocations(long userId)
    {
        return await context.Filaments
            .Where(f => f.CreatedById == userId)
            .Where(f => f.StorageLocation != null && f.StorageLocation != "")
            .Select(f => f.StorageLocation!)
            .Distinct()
            .OrderBy(s => s)
            .ToArrayAsync();
    }

    public async Task<string[]> GetFilamentPurchaseLocations(long userId)
    {
        return await context.Filaments
            .Where(f => f.CreatedById == userId)
            .Where(f => f.PurchaseLocation != null && f.PurchaseLocation != "")
            .Select(f => f.PurchaseLocation!)
            .Distinct()
            .OrderBy(s => s)
            .ToArrayAsync();
    }

    public async Task<string[]> GetFilamentBrands(long userId)
    {
        return await context.Filaments
            .Where(f => f.CreatedById == userId)
            .Where(f => f.Brand != null && f.Brand != "")
            .Select(f => f.Brand!)
            .Distinct()
            .OrderBy(s => s)
            .ToArrayAsync();
    }

    public async Task<bool> CanUserAccessFilament(long userId, Guid filamentId)
    {
        var filament = await GetFilamentById(filamentId);
        if (filament == null)
        {
            return false;
        }

        // Only the user that created the filament can access it.
        return filament.CreatedById == userId;
    }

    public async Task<bool> CanUserAccessAllFilaments(long userId, IEnumerable<Guid> filamentIds)
    {
        var ids = filamentIds.Distinct().ToList();
        if (ids.Count == 0) return true;

        var accessibleCount = await context.Filaments
            .CountAsync(f => ids.Contains(f.Id) && f.CreatedById == userId);

        return accessibleCount == ids.Count;
    }

    /// <summary>
    /// Delete a Filament if that filament isn't in use by a print.
    /// </summary>
    /// <param name="filamentId"></param>
    /// <returns></returns>
    public async Task DeleteFilament(Guid filamentId)
    {
        var filament = await GetFilamentById(filamentId);
        if (filament == null)
        {
            return;
        }

        // Check if any filament is being used.
        if (filament.PrintFilaments!.Any())
        {
            throw new FilamentIsInUseException();
        }

        // Remove any adjustments
        if (filament.FilamentAdjustments!.Any())
        {
            context.FilamentAdjustments.RemoveRange(filament.FilamentAdjustments!);
        }

        context.Filaments.Remove(filament);
        await context.SaveChangesAsync();

        telemetry.TrackEvent("FilamentDelete");

        return;
    }

    public bool FilamentExists(Guid id)
    {
        return context.Filaments.Any(f => f.Id == id);
    }

}
