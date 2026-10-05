using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Printer;

namespace PrintLogApi.Services;

public class PrinterService(
    PrintLogContext context,
    TelemetryClient telemetry,
    IBlobStorageService blobStorage,
    ILogger<PrinterService> logger,
    ICacheVersionService cacheVersionService) : IPrinterService
{
    /// <summary>
    /// The category a printer gets when none is named. Shared with PrintersController so the web
    /// and MCP create paths cannot drift onto different defaults.
    /// </summary>
    public const string DefaultPrinterCategoryNickname = "FFF";

    // Bucketed expiry: repeated signing within a window returns a byte-identical URL, which
    // is what lets the browser image cache hit at all. Max-age is strictly less than the
    // bucket so a cached response cannot outlive the signature that fetched it.
    private static readonly TimeSpan SasBucket = TimeSpan.FromHours(6);
    private static readonly TimeSpan SasCacheMaxAge = TimeSpan.FromHours(5);

    /// <summary>
    /// The per-tier image cap. There is no IsProAsync helper anywhere in this codebase; the
    /// established test is this direct query, and wrapping it in a new service method would
    /// only create a second definition of "is Pro."
    /// </summary>
    public async Task<int> GetMaxImagesPerPrinter(long userId)
    {
        var subscription = await context.Subscriptions
            .Where(s => s.UserId == userId)
            .AsNoTracking()
            .SingleOrDefaultAsync();

        return subscription?.Status == SubscriptionStatus.Active
            ? SubscriptionLimits.ProMaxImages
            : SubscriptionLimits.FreeMaxImages;
    }

    /// <summary>
    /// Signs the full image set for one printer detail response. Explicit and
    /// post-materialization, never a projection: SAS signing cannot run inside ProjectTo.
    /// </summary>
    public async Task HydrateDetailImageUrlsAsync(
        PrinterDetailDto detail, CancellationToken ct = default)
    {
        if (detail.Id is not { } printerId) return;

        var images = await context.PrinterImages
            .AsNoTracking()
            .Where(pi => pi.PrinterId == printerId)
            // (DisplayOrder, Id): two concurrent uploads may share a DisplayOrder, so
            // ordering on it alone is not deterministic.
            .OrderBy(pi => pi.DisplayOrder).ThenBy(pi => pi.Id)
            .Select(pi => new
            {
                pi.Id,
                pi.IsDefault,
                pi.DisplayOrder,
                pi.ContentType,
                OriginalPath = pi.File.Path,
                ThumbnailPath = pi.ThumbnailFile != null ? pi.ThumbnailFile.Path : null
            })
            .ToListAsync(ct);

        // The filtered unique index enforces AT MOST one default, never at least one. A
        // delete interleaved with an add can leave a non-empty set with none; heal it on
        // read rather than serializing those transactions.
        var defaultId = images.FirstOrDefault(i => i.IsDefault)?.Id ?? images.FirstOrDefault()?.Id;

        var hydrated = new List<PrinterImageDto>(images.Count);
        foreach (var image in images)
        {
            var dto = new PrinterImageDto
            {
                Id = image.Id,
                IsDefault = image.Id == defaultId,
                DisplayOrder = image.DisplayOrder
            };

            dto.Url = await SignImageOrNullAsync(image.OriginalPath, image.ContentType, printerId, ct);

            // The thumbnail is WebP; the original is whatever the decode produced. Signing an
            // original as "image/webp" would set a response content type the bytes contradict.
            dto.ThumbnailUrl = image.ThumbnailPath is null
                ? dto.Url
                : await SignImageOrNullAsync(image.ThumbnailPath, "image/webp", printerId, ct);

            hydrated.Add(dto);
        }

        detail.Images = hydrated;
    }

    /// <summary>
    /// Signs one just-uploaded image into its response DTO. The upload response would
    /// otherwise carry no usable URL.
    /// </summary>
    public async Task<PrinterImageDto> HydrateImageDtoAsync(
        PrinterImage image, CancellationToken ct = default)
    {
        // The File rows were just written by PrinterImageService inside this same scope, so
        // read them back rather than assuming the navigations were loaded.
        var paths = await context.PrinterImages
            .AsNoTracking()
            .Where(pi => pi.Id == image.Id)
            .Select(pi => new
            {
                OriginalPath = pi.File.Path,
                ThumbnailPath = pi.ThumbnailFile != null ? pi.ThumbnailFile.Path : null
            })
            .FirstOrDefaultAsync(ct);

        var dto = new PrinterImageDto
        {
            Id = image.Id,
            IsDefault = image.IsDefault,
            DisplayOrder = image.DisplayOrder
        };

        if (paths is null) return dto;

        dto.Url = await SignImageOrNullAsync(paths.OriginalPath, image.ContentType, image.PrinterId, ct);
        dto.ThumbnailUrl = paths.ThumbnailPath is null
            ? dto.Url
            : await SignImageOrNullAsync(paths.ThumbnailPath, "image/webp", image.PrinterId, ct);

        return dto;
    }

    /// <summary>
    /// Signs one blob path, or returns null if signing fails. Public because the printer
    /// summary endpoint signs AFTER its HybridCache read, outside this service.
    /// </summary>
    public async Task<string?> SignImageOrNullAsync(
        string? path, string contentType, long printerId, CancellationToken ct = default)
    {
        if (path is null) return null;

        try
        {
            return (await blobStorage.GenerateSasInlineUrlAsync(
                BlobContainers.PrinterImages, Path.GetFileName(path),
                contentType, SasBucket, SasCacheMaxAge)).ToString();
        }
        catch (Exception ex)
        {
            // Each URL is signed independently so one unsignable blob costs one image,
            // not the whole response.
            logger.LogWarning(ex, "Failed to sign printer image URL for {PrinterId}", printerId);
            return null;
        }
    }

    public async Task<Printer?> getPrinterById(long printerId)
    {
        var existingPrinter = await context.Printers
            .Include(p => p.LoadedFilaments!)
                .ThenInclude(f => f.Filament)
            .Include(p => p.Category!)
                .ThenInclude(type => type.MaterialCategory)
            .Where(p => p.Id == printerId)
            .AsSplitQuery()
            .SingleOrDefaultAsync();

        return existingPrinter;
    }

    /// <summary>
    /// Sets a list of loadedFilamentIds as a printer's currently loaded filaments.
    /// </summary>
    /// <param name="printerId"></param>
    /// <param name="loadedFilamentIds"></param>
    /// <returns></returns>
    public async Task setLoadedFilament(long printerId, IEnumerable<Guid> loadedFilamentIds)
    {
        var printer = await getPrinterById(printerId);

        if (printer == null)
        {
            throw new PrinterDoesNotExistException();
        }

        // Handle managing loaded filaments for this printer.
        // Unload any filament that isn't in the new print.
        var currentlyLoadedFilament = printer.LoadedFilaments!;
        var filamentsToUnload = currentlyLoadedFilament
            .Where(f => !loadedFilamentIds.Any(id => id == f.FilamentId));

        var modifiedTime = DateTimeOffset.Now;
        foreach (var filament in filamentsToUnload)
        {
            filament.UnloadedDateTime = modifiedTime;
            context.Entry(filament).State = EntityState.Modified;
        }

        // Add any new filament to the list
        var newFilament = loadedFilamentIds
            .Where(loadedFilamentId => !currentlyLoadedFilament.Any(f => f.FilamentId == loadedFilamentId));

        foreach (var filamentId in newFilament)
        {
            if (filamentId != default)
            {
                var newLoadedFilament = new PrinterFilament
                {
                    FilamentId = filamentId,
                    PrinterId = printer.Id,
                    LoadedDateTime = modifiedTime,
                };
                printer.LoadedFilaments!.Add(newLoadedFilament);
            }
        }

        // Fixup for any loaded filaments with no set LoadedDateTime:
        foreach (var pf in printer.LoadedFilaments!.Where(lf => lf.LoadedDateTime == default(DateTimeOffset)))
        {
            pf.LoadedDateTime = modifiedTime;
        }

        // Finally, unload these filament from any other printer's loaded list.
        var loadedFilamentFromOtherPrinters = await context
            .PrinterFilament
            .Where(pf => pf.PrinterId != printerId && loadedFilamentIds.Any(id => id == pf.FilamentId))
            .ToListAsync();

        foreach (var filament in loadedFilamentFromOtherPrinters)
        {
            filament.UnloadedDateTime = modifiedTime;
        }

    }

    /// <summary>
    /// Delete a Printer if that printer isn't in use by a print.
    /// </summary>
    /// <param name="printerId"></param>
    /// <returns></returns>
    public async Task DeletePrinter(long printerId)
    {
        var printer = await getPrinterById(printerId);
        if (printer == null)
        {
            return;
        }

        var hasExistingPrints = await DoPrintsExistForPrinter(printerId);


        // Check if any filament is being used.
        if (hasExistingPrints)
        {
            throw new PrinterIsInUseException();
        }

        // Remove any adjustments
        if (printer.LoadedFilaments!.Any())
        {
            context.PrinterFilament.RemoveRange(printer.LoadedFilaments!);
        }

        var maintenanceEntries = context.PrinterMaintenance.Where(pm => pm.PrinterId == printerId);
        if (maintenanceEntries.Any())
        {
            context.PrinterMaintenance.RemoveRange(maintenanceEntries);
        }

        // Images, their File rows and their blobs. Ordering is load-bearing: the
        // PrinterImage -> File FKs are Restrict, so the image rows must go before the files,
        // and both before the printer. Without this the delete would FAIL on the Restrict FK
        // rather than orphan anything - broken either way.
        var images = await context.PrinterImages
            .Include(pi => pi.File)
            .Include(pi => pi.ThumbnailFile)
            .Where(pi => pi.PrinterId == printerId)
            .ToListAsync();

        var blobNames = images
            .SelectMany(pi => new[] { pi.File?.Path, pi.ThumbnailFile?.Path })
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.GetFileName(path)!)
            .ToList();

        foreach (var image in images)
        {
            context.PrinterImages.Remove(image);
            if (image.File is not null) context.Files.Remove(image.File);
            if (image.ThumbnailFile is not null) context.Files.Remove(image.ThumbnailFile);
        }

        context.Printers.Remove(printer);

        await context.SaveChangesAsync();

        // Blobs LAST, so a failed save leaves an orphaned blob rather than a row pointing at
        // destroyed bytes. Failures here are logged and swallowed for the same reason: the
        // database is already consistent and the printer is gone.
        foreach (var name in blobNames)
        {
            try
            {
                await blobStorage.DeleteBlobAsync(BlobContainers.PrinterImages, name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete printer image blob {BlobName}; continuing", name);
            }
        }

        telemetry.TrackEvent("PrinterDelete");

        return;
    }

    private async Task<bool> DoPrintsExistForPrinter(long printerId)
    {
        var exists = await context.Prints.AnyAsync(p => p.PrinterId == printerId);

        return exists;
    }

    /// <summary>
    /// Cap on the loaded spools returned by get_printer. An AMS/toolchanger setup can carry
    /// several at once; the count and truncation flag make any omission visible, because
    /// silently dropping a loaded spool from "what is loaded right now" is a wrong answer.
    /// </summary>
    public const int MaxLoadedFilaments = 10;

    public async Task<McpPage<PrinterListItem>> ListPrintersForMcp(
        long userId, int page, int pageSize, CancellationToken ct)
    {
        // Printer ownership is UserId (Filament ownership is CreatedById - they differ).
        var query = context.Printers.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PrinterListItem(
                p.Id, p.Name!, p.Make, p.Model, p.NozzleDiameter, p.IsActive))
            .ToListAsync(ct);

        var totalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0;
        return new McpPage<PrinterListItem>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<PrinterDetailResult> GetPrinterForMcp(
        long userId, long printerId, CancellationToken ct)
    {
        var row = await context.Printers.AsNoTracking()
            .Where(p => p.Id == printerId && p.UserId == userId) // creator-only; no existence oracle
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Make,
                p.Model,
                p.Description,
                p.CategoryNickname,
                p.NozzleDiameter,
                p.BedWidthMm,
                p.BedDepthMm,
                p.BedHeightMm,
                p.HasHeatedBed,
                p.HasHeatedChamber,
                p.WattageW,
                p.IsActive,
                p.FilamentDiameter,
                p.BeamDiameter,
                p.ScreenResolutionXPixels,
                p.ScreenResolutionYPixels,

                // "Loaded" means CURRENTLY loaded. PrinterFilament keeps historical rows, so
                // without the UnloadedDateTime filter every spool ever mounted would be reported
                // as loaded now.
                LoadedCount = p.LoadedFilaments!.Count(pf =>
                    pf.UnloadedDateTime == null && pf.Filament.CreatedById == userId),

                // A corrupt row can reference another user's spool. Its material, colour and
                // remaining weight all live on that foreign row, so a redacted entry would carry
                // no usable information - exclude it, but count it so the omission is visible.
                ExcludedCount = p.LoadedFilaments!.Count(pf =>
                    pf.UnloadedDateTime == null && pf.Filament.CreatedById != userId),

                Loaded = p.LoadedFilaments!
                    .Where(pf => pf.UnloadedDateTime == null && pf.Filament.CreatedById == userId)
                    .OrderByDescending(pf => pf.LoadedDateTime)
                    .ThenBy(pf => pf.Id)
                    .Select(pf => new
                    {
                        pf.FilamentId,
                        Name = pf.Filament.DisplayName,
                        Brand = pf.Filament.Brand,
                        Material = pf.Filament.MaterialType,
                        Color = pf.Filament.ColorName,
                        pf.Filament.DiameterMm,

                        // Same remaining-weight expression AutoMapper uses for FilamentSummaryDto:
                        // initial weight, minus usage (actual, falling back to estimate), plus
                        // adjustments.
                        RemainingMg = (pf.Filament.InitialNominalWeightMg ?? 0)
                            - pf.Filament.PrintFilaments!.Sum(u =>
                                u.AmountMg.HasValue && u.AmountMg > 0 ? (long)u.AmountMg
                                : u.EstimatedAmountMg.HasValue && u.EstimatedAmountMg > 0 ? (long)u.EstimatedAmountMg
                                : 0L)
                            + pf.Filament.FilamentAdjustments!.Sum(adj => adj.AmountMg),

                        pf.LoadedDateTime,
                    })
                    // Capped in SQL, not after materialization: LoadedCount above already
                    // reports the true total, so there is no reason to pull every row back.
                    .Take(MaxLoadedFilaments)
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            throw McpToolException.NotFound("Printer not found.");
        }

        var loaded = row.Loaded
            .Select(f => new LoadedFilament(
                f.FilamentId, f.Name, f.Brand, f.Material, f.Color, f.DiameterMm,
                McpUnits.MgToGrams(f.RemainingMg), f.LoadedDateTime))
            .ToList();

        // Named arguments deliberately: this record has 22 fields, many of them double? or
        // bool?, so a positional mix-up would compile cleanly and silently return the bed depth
        // as the bed width.
        return new PrinterDetailResult(
            Id: row.Id,
            // Name is non-null in this contract but only length-limited on the entity, so a
            // legacy row can hold null. Normalize rather than throw.
            Name: row.Name ?? string.Empty,
            Make: row.Make,
            Model: row.Model,
            Description: row.Description,
            CategoryNickname: row.CategoryNickname,
            NozzleDiameterMm: row.NozzleDiameter,
            BedWidthMm: row.BedWidthMm,
            BedDepthMm: row.BedDepthMm,
            BedHeightMm: row.BedHeightMm,
            HasHeatedBed: row.HasHeatedBed,
            HasHeatedChamber: row.HasHeatedChamber,
            WattageW: row.WattageW,
            IsActive: row.IsActive,
            LoadedFilaments: loaded,
            LoadedFilamentCount: row.LoadedCount,
            LoadedFilamentsTruncated: row.LoadedCount > MaxLoadedFilaments,
            ExcludedUnreadableSpools: row.ExcludedCount,
            FilamentDiameterMm: row.FilamentDiameter,
            BeamDiameterMm: row.BeamDiameter,
            ScreenResolutionXPixels: row.ScreenResolutionXPixels,
            ScreenResolutionYPixels: row.ScreenResolutionYPixels);
    }

    /// <summary>
    /// Resolves the category for a create. An unknown nickname is rejected rather than falling
    /// back to the default: filing a printer under a category the caller did not ask for is a
    /// wrong answer that reads like a right one.
    /// </summary>
    private async Task<PrinterCategory> RequirePrinterCategory(string? nickname, CancellationToken ct)
    {
        var requested = nickname ?? DefaultPrinterCategoryNickname;
        var category = await context.PrinterCategories
            .FirstOrDefaultAsync(c => c.Nickname == requested, ct);
        if (category == null)
        {
            // Name the valid options: nothing lists printer categories, so a bare rejection
            // leaves an agent guessing. They are a small fixed seed shared by every user, so the
            // extra query costs nothing on the happy path and only runs when already failing.
            var known = await context.PrinterCategories
                .Select(c => c.Nickname).OrderBy(n => n).ToListAsync(ct);
            throw McpToolException.InvalidArguments(
                $"'{requested}' is not a known printer category. Valid categories: {string.Join(", ", known)}.");
        }
        return category;
    }

    private static void RequireCreateFields(PrinterAttributesInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Make))
        {
            throw McpToolException.InvalidArguments("make is required.");
        }
        if (string.IsNullOrWhiteSpace(input.Model))
        {
            throw McpToolException.InvalidArguments("model is required.");
        }
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw McpToolException.InvalidArguments("name is required.");
        }
    }

    private static string? RequireIdempotencyKey(string? key)
    {
        if (key == null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            throw McpToolException.InvalidArguments("idempotencyKey cannot be blank.");
        }
        // Trim BEFORE the length check: the trimmed value is what gets stored and compared, so
        // that is the value the limit applies to.
        var trimmed = key.Trim();
        McpWriteValidation.RequireMaxLength(trimmed, 200, "idempotencyKey");
        return trimmed;
    }

    public async Task<CreatePrinterResult> CreatePrinterForMcp(
        long userId, PrinterAttributesInput input, string? idempotencyKey, CancellationToken ct)
    {
        const string toolName = "create_printer";

        // Canonicalize ONCE, before both hashing and persistence. Anything the fingerprint
        // normalizes away must also be normalized in what we store, or the hash asserts an
        // equivalence the database contradicts.
        input = input.Canonicalize();
        RequireCreateFields(input);
        McpPrinterValidation.ValidateAttributes(input);
        idempotencyKey = RequireIdempotencyKey(idempotencyKey);

        string? fingerprint = null;
        if (idempotencyKey != null)
        {
            fingerprint = McpRequestFingerprint.ComputeCreatePrinter(input);
            var replay = await FindIdempotentPrinter(userId, toolName, idempotencyKey, fingerprint, ct);
            if (replay != null)
            {
                return replay;
            }
        }

        var category = await RequirePrinterCategory(input.CategoryNickname, ct);

        var printer = new Printer
        {
            UserId = userId,
            Make = input.Make,
            Model = input.Model,
            Name = input.Name,
            Description = input.Description,
            Category = category,
            CategoryNickname = category.Nickname,
            NozzleDiameter = input.NozzleDiameterMm,
            FilamentDiameter = input.FilamentDiameterMm,
            BeamDiameter = input.BeamDiameterMm,
            BedWidthMm = input.BedWidthMm,
            BedDepthMm = input.BedDepthMm,
            BedHeightMm = input.BedHeightMm,
            ScreenResolutionXPixels = input.ScreenResolutionXPixels,
            ScreenResolutionYPixels = input.ScreenResolutionYPixels,
            HasHeatedBed = input.HasHeatedBed,
            HasHeatedChamber = input.HasHeatedChamber,
            WattageW = input.WattageW,
            // MCP-only semantic: the website DTO's IsActive is a non-nullable bool (omitted =>
            // false), but a printer someone just created is presumably in use. Consistent with
            // create_material; a deliberate divergence, not website parity.
            IsActive = input.IsActive ?? true,
            // No LoadedFilaments, deliberately: an empty collection here would still be an
            // assertion about loaded state. Leaving it unset says nothing at all.
        };

        if (idempotencyKey == null)
        {
            context.Printers.Add(printer);
            await context.SaveChangesAsync(ct);
        }
        else
        {
            try
            {
                await CreatePrinterWithIdempotencyRecord(printer, userId, idempotencyKey, fingerprint, ct);
            }
            catch (DbUpdateException)
            {
                // Possible unique-index race: another identical call created the printer first.
                // The transaction rolled back but the failed Added entities are still tracked;
                // clear them so the recovery query reads only committed state, then replay the
                // winner's result. If there is NO such record the failure was something else
                // entirely — rethrow rather than reporting every write failure as an
                // idempotency problem.
                context.ChangeTracker.Clear();
                var concurrent = await FindIdempotentPrinter(userId, toolName, idempotencyKey, fingerprint, ct);
                if (concurrent != null)
                {
                    return concurrent;
                }
                throw;
            }
        }

        telemetry.TrackEvent("McpPrinterAdded");
        cacheVersionService.InvalidateUserCache(userId);

        // Re-read through the read projection rather than mapping the tracked entity: it is the
        // one place that knows how to render loaded filaments (which this path deliberately
        // never loads), so create, update and get_printer can never drift in shape.
        return new CreatePrinterResult(
            await GetPrinterForMcp(userId, printer.Id, ct), WasReplayed: false);
    }

    /// <summary>
    /// Creates the printer and its idempotency record atomically. Lets DbUpdateException escape:
    /// only the caller can tell a lost unique-index race (replayable) from a genuine write
    /// failure (not), because only it knows the key and fingerprint to look the winner up with.
    /// </summary>
    private async Task CreatePrinterWithIdempotencyRecord(
        Printer printer, long userId, string key, string? fingerprint, CancellationToken ct)
    {
        // SqlServerRetryingExecutionStrategy forbids user-initiated transactions unless they
        // run inside an execution strategy, so the whole tx is the retriable unit.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            using var tx = await context.Database.BeginTransactionAsync(ct);
            context.Printers.Add(printer);
            await context.SaveChangesAsync(ct); // assigns printer.Id

            context.McpIdempotencyRecords.Add(
                McpIdempotencyRecordFactory.ForPrinter(userId, key, fingerprint, printer.Id));
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    private async Task<CreatePrinterResult?> FindIdempotentPrinter(
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

        // Reads only its OWN target field. A record scoped to this tool with no CreatedPrinterId
        // is dangling, whatever else it may carry.
        // Short-circuit order is load-bearing: the null test stays on the left so the query is
        // still skipped entirely when the id is absent.
        if (record.CreatedPrinterId is not { } printerId
            || !await context.Printers.AnyAsync(p => p.Id == printerId && p.UserId == userId, ct))
        {
            throw McpToolException.NotFound("The prior result for this idempotency key no longer exists.");
        }

        return new CreatePrinterResult(
            await GetPrinterForMcp(userId, printerId, ct), WasReplayed: true);
    }

    /// <summary>
    /// Rejects a request that both sets and clears the same field. Guessing which one won would
    /// make one of the caller's two explicit arguments silently disappear.
    /// </summary>
    private static void RequireNoSetAndClearCollision(PrinterAttributesInput input, ISet<string> clear)
    {
        void Check(string field, bool isSet)
        {
            if (isSet && clear.Contains(field))
            {
                throw McpToolException.InvalidArguments($"{field} cannot be both set and cleared.");
            }
        }

        Check("description", input.Description != null);
        Check("nozzleDiameterMm", input.NozzleDiameterMm.HasValue);
        Check("filamentDiameterMm", input.FilamentDiameterMm.HasValue);
        Check("beamDiameterMm", input.BeamDiameterMm.HasValue);
        Check("bedWidthMm", input.BedWidthMm.HasValue);
        Check("bedDepthMm", input.BedDepthMm.HasValue);
        Check("bedHeightMm", input.BedHeightMm.HasValue);
        Check("screenResolutionXPixels", input.ScreenResolutionXPixels.HasValue);
        Check("screenResolutionYPixels", input.ScreenResolutionYPixels.HasValue);
        Check("hasHeatedBed", input.HasHeatedBed.HasValue);
        Check("hasHeatedChamber", input.HasHeatedChamber.HasValue);
        Check("wattageW", input.WattageW.HasValue);
    }

    public async Task<PrinterDetailResult> UpdatePrinterForMcp(
        long userId, long printerId, PrinterAttributesInput input, ISet<string>? clear, CancellationToken ct)
    {
        clear ??= new HashSet<string>();

        // Canonicalize ONCE, before validation and persistence — same rule as create.
        input = input.Canonicalize();
        McpPrinterValidation.ValidateAttributes(input);
        // Enforced HERE, not only in the tool wrapper: the service is the boundary every caller
        // goes through, so this is where "make is not clearable" is actually true.
        McpPrinterValidation.RequireClearableFields(clear);
        RequireNoSetAndClearCollision(input, clear);

        // No Include of LoadedFilaments, deliberately: what is never loaded can never be marked
        // modified, so the loaded-state invariant does not depend on the patch code being careful.
        var printer = await context.Printers
            .FirstOrDefaultAsync(p => p.Id == printerId && p.UserId == userId, ct);
        if (printer == null)
        {
            throw McpToolException.NotFound("Printer not found.");
        }

        // Resolve the category BEFORE any mutation: an unknown nickname must reject the whole
        // patch, not leave the earlier fields applied. An omitted category is left alone, legacy
        // null included — force-repairing it would be an edit nobody asked for.
        if (input.CategoryNickname != null)
        {
            var category = await RequirePrinterCategory(input.CategoryNickname, ct);
            printer.Category = category;
            printer.CategoryNickname = category.Nickname;
        }

        PatchString(v => printer.Make = v, input.Make);
        PatchString(v => printer.Model = v, input.Model);
        PatchString(v => printer.Name = v, input.Name);
        PatchString(v => printer.Description = v, input.Description, clear.Contains("description"));

        PatchValue(v => printer.NozzleDiameter = v, input.NozzleDiameterMm, clear.Contains("nozzleDiameterMm"));
        PatchValue(v => printer.FilamentDiameter = v, input.FilamentDiameterMm, clear.Contains("filamentDiameterMm"));
        PatchValue(v => printer.BeamDiameter = v, input.BeamDiameterMm, clear.Contains("beamDiameterMm"));
        PatchValue(v => printer.BedWidthMm = v, input.BedWidthMm, clear.Contains("bedWidthMm"));
        PatchValue(v => printer.BedDepthMm = v, input.BedDepthMm, clear.Contains("bedDepthMm"));
        PatchValue(v => printer.BedHeightMm = v, input.BedHeightMm, clear.Contains("bedHeightMm"));
        PatchValue(v => printer.ScreenResolutionXPixels = v, input.ScreenResolutionXPixels, clear.Contains("screenResolutionXPixels"));
        PatchValue(v => printer.ScreenResolutionYPixels = v, input.ScreenResolutionYPixels, clear.Contains("screenResolutionYPixels"));
        PatchValue(v => printer.HasHeatedBed = v, input.HasHeatedBed, clear.Contains("hasHeatedBed"));
        PatchValue(v => printer.HasHeatedChamber = v, input.HasHeatedChamber, clear.Contains("hasHeatedChamber"));
        PatchValue(v => printer.WattageW = v, input.WattageW, clear.Contains("wattageW"));

        if (input.IsActive.HasValue)
        {
            printer.IsActive = input.IsActive.Value;
        }

        await context.SaveChangesAsync(ct);

        telemetry.TrackEvent("McpPrinterEdit");
        cacheVersionService.InvalidateUserCache(userId);

        return await GetPrinterForMcp(userId, printer.Id, ct);
    }

    private static void PatchString(Action<string?> set, string? value, bool clear = false)
    {
        if (clear)
        {
            set(null);
        }
        else if (value != null)
        {
            set(value);
        }
    }

    private static void PatchValue<T>(Action<T?> set, T? value, bool clear) where T : struct
    {
        if (clear)
        {
            set(null);
        }
        else if (value.HasValue)
        {
            set(value);
        }
    }
}
