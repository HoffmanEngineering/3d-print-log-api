using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Caching;
using PrintLogApi.Models;
using PrintLogApi.Profiles;

namespace PrintLogApi.Services.Spoolman;

public interface ISpoolmanInventoryService
{
    /// <summary>The user's Spoolman view: every filament-category spool, archived ones included, and its vendors.</summary>
    Task<SpoolmanInventory> GetInventoryAsync(long userId, CancellationToken ct);
}

/// <summary>
/// Builds a user's Spoolman view of their inventory (#177), cached per user in HybridCache with
/// the user's <see cref="ICacheVersionService"/> GUID in the key, like the summary endpoints.
/// Mainsail and Fluidd re-read <c>/spool</c> often and unpaginated, so the whole inventory is one
/// entry and every filter, sort and page is applied to it in memory: one query per change to the
/// user's data, not one per refresh. Filament and print writes bump the version.
///
/// <para><b>Mapping.</b> Spoolman's ids are integers: <c>spool.id</c> and <c>filament.id</c> are both
/// the spool's <see cref="Filament.SpoolNumber"/> (one entity per spool here, so one filament per
/// spool), and <c>vendor.id</c> is <see cref="VendorId"/>. Weights are milligrams to grams and
/// lengths metres to millimetres, using the same remaining and used figures as the web app
/// (<see cref="FilamentProfile.RemainingMg"/>, <see cref="FilamentProfile.UsedMg"/>). Only the
/// <c>filament</c> material category is a spool; resin and powder are left out.</para>
/// </summary>
public sealed class SpoolmanInventoryService(
    PrintLogContext context,
    HybridCache cache,
    CachedComputation computation,
    ICacheVersionService cacheVersion,
    IFilamentService filaments) : ISpoolmanInventoryService
{
    /// <summary>The material category that holds spools of filament.</summary>
    public const string FilamentCategory = "filament";

    /// <summary>Spoolman's own default density, used only when a spool records none.</summary>
    public const double FallbackDensity = 1.24;

    /// <summary>Used only when a spool records no diameter; Spoolman requires one.</summary>
    public const double FallbackDiameter = 1.75;

    public async Task<SpoolmanInventory> GetInventoryAsync(long userId, CancellationToken ct)
    {
        // Numbered here, outside the cached factory: it writes, and only a spool the previous
        // app version created during the #174 migration can need it. One indexed query otherwise.
        await filaments.NumberUnnumberedSpoolsAsync(userId, ct);

        var key = $"spoolman_inventory:{userId}:{cacheVersion.GetUserCacheVersion(userId)}";

        // CachedComputation's scope and HybridCache's token: see CachedComputation.
        return await cache.GetOrCreateAsync(
            key,
            (computation, userId),
            static (state, token) => state.computation.RunAsync(
                (services, innerToken) => LoadAsync(services.GetRequiredService<PrintLogContext>(), state.userId, innerToken),
                token),
            cancellationToken: ct);
    }

    private static async Task<SpoolmanInventory> LoadAsync(PrintLogContext db, long userId, CancellationToken ct)
    {
        var rows = await db.Filaments
            .AsNoTracking()
            .Where(f => f.CreatedById == userId
                && f.MaterialCategoryNickname == FilamentCategory
                && f.SpoolNumber != null)
            .Select(Projection)
            .ToListAsync(ct);

        // First and last use from the prints, as dates only. Grouped in memory rather than with
        // Min/Max in SQL, which SQLite cannot translate over DateTimeOffset.
        var uses = await db.PrintFilament
            .AsNoTracking()
            .Where(pf => pf.Filament != null && pf.Filament.CreatedById == userId && pf.Filament.MaterialCategoryNickname == FilamentCategory)
            .Select(pf => new { pf.FilamentId, pf.Print.StartDate, pf.Print.CreatedDate })
            .ToListAsync(ct);
        var useDates = uses
            .Where(u => u.FilamentId.HasValue)
            .GroupBy(u => u.FilamentId!.Value)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var dates = g.Select(u => u.StartDate?.UtcDateTime ?? DateTime.SpecifyKind(u.CreatedDate, DateTimeKind.Utc)).ToList();
                    return (First: dates.Min(), Last: dates.Max());
                });

        var vendors = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Brand))
            .GroupBy(r => NormalizeBrand(r.Brand!))
            .Select(g => new SpoolmanVendor(VendorId(g.Key), Utc(g.Min(r => r.CreatedDate)), g.OrderBy(r => r.CreatedDate).First().Brand!.Trim()))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var vendorsByKey = vendors.ToDictionary(v => NormalizeBrand(v.Name));

        var spools = rows
            .OrderBy(r => r.SpoolNumber)
            .Select(r => ToSpool(r, r.Brand is { } brand && !string.IsNullOrWhiteSpace(brand) ? vendorsByKey[NormalizeBrand(brand)] : null,
                useDates.TryGetValue(r.Id, out var used) ? used : null))
            .ToList();

        return new SpoolmanInventory(spools, vendors);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Filament, SourceRow>> Projection = BuildProjection();

    private static System.Linq.Expressions.Expression<Func<Filament, SourceRow>> BuildProjection()
    {
        // Composes the two shared figures into one projection, so the SQL computes remaining and
        // used exactly as the detail page does.
        var f = System.Linq.Expressions.Expression.Parameter(typeof(Filament), "f");
        var remaining = Replace(FilamentProfile.RemainingMg.Body, FilamentProfile.RemainingMg.Parameters[0], f);
        var used = Replace(FilamentProfile.UsedMg.Body, FilamentProfile.UsedMg.Parameters[0], f);

        System.Linq.Expressions.Expression<Func<Filament, long?, long, SourceRow>> shape = (x, remainingMg, usedMg) => new SourceRow
        {
            Id = x.Id,
            SpoolNumber = x.SpoolNumber!.Value,
            DisplayName = x.DisplayName,
            Brand = x.Brand,
            MaterialType = x.MaterialType,
            Density = x.MaterialDensityGramPerCubicCm,
            DiameterMm = x.DiameterMm,
            ColorHex = x.ColorHex,
            Colors = x.Colors,
            RecommendedTemp = x.RecommendedTemp,
            RecommendedBedTemp = x.RecommendedBedTemp,
            IsActive = x.IsActive,
            StorageLocation = x.StorageLocation,
            Notes = x.Notes,
            PurchasePriceValue = x.PurchasePriceValue,
            CreatedDate = x.CreatedDate,
            InitialNominalWeightMg = x.InitialNominalWeightMg,
            SpoolWeightMg = x.SpoolWeightMg,
            RemainingMg = remainingMg,
            UsedMg = usedMg,
        };

        var body = Replace(shape.Body, shape.Parameters[0], f);
        body = Replace(body, shape.Parameters[1], remaining);
        body = Replace(body, shape.Parameters[2], used);
        return System.Linq.Expressions.Expression.Lambda<Func<Filament, SourceRow>>(body, f);
    }

    internal static SpoolmanSpool ToSpool(SourceRow row, SpoolmanVendor? vendor, (DateTime First, DateTime Last)? used)
    {
        var density = row.Density > 0 ? row.Density : FallbackDensity;
        var diameter = row.DiameterMm is > 0 ? row.DiameterMm.Value : FallbackDiameter;
        var registered = Utc(row.CreatedDate);
        var initialGrams = Grams(row.InitialNominalWeightMg);
        var remainingGrams = row.RemainingMg.HasValue ? Math.Max(0, row.RemainingMg.Value / 1000.0) : (double?)null;
        var usedGrams = Math.Max(0, row.UsedMg / 1000.0);
        var (colorHex, multiColorHexes) = Colors(row);

        var filament = new SpoolmanFilament(row.SpoolNumber, registered, density, diameter)
        {
            Name = row.DisplayName,
            Vendor = vendor,
            Material = string.IsNullOrWhiteSpace(row.MaterialType) ? null : row.MaterialType,
            Weight = initialGrams is > 0 ? initialGrams : null,
            SpoolWeight = Grams(row.SpoolWeightMg),
            SettingsExtruderTemp = Temperature(row.RecommendedTemp),
            SettingsBedTemp = Temperature(row.RecommendedBedTemp),
            ColorHex = colorHex,
            MultiColorHexes = multiColorHexes,
            MultiColorDirection = multiColorHexes is null ? null : "coaxial",
        };

        return new SpoolmanSpool(
            row.SpoolNumber,
            registered,
            filament,
            usedGrams,
            LengthMm(usedGrams, density, diameter),
            Archived: !row.IsActive)
        {
            FirstUsed = used?.First,
            LastUsed = used?.Last,
            Price = Price(row.PurchasePriceValue),
            RemainingWeight = remainingGrams,
            InitialWeight = initialGrams,
            SpoolWeight = Grams(row.SpoolWeightMg),
            RemainingLength = remainingGrams.HasValue ? LengthMm(remainingGrams.Value, density, diameter) : null,
            Location = string.IsNullOrWhiteSpace(row.StorageLocation) ? null : row.StorageLocation,
            Comment = string.IsNullOrWhiteSpace(row.Notes) ? null : row.Notes,
        };
    }

    /// <summary>
    /// A stable per-user vendor id for a brand: a positive 31-bit hash of its normalized name.
    /// Derived from the name alone, not from first use, so it cannot shift when the spool that
    /// introduced a brand is deleted; a client that filtered or remembered by vendor id keeps
    /// working. 31 bits keeps it a plain int in every client. Collisions between two brands of one
    /// user are possible in principle and harmless in practice: they would share a vendor id.
    /// </summary>
    public static int VendorId(string brand)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeBrand(brand)));
        var id = (int)(BitConverter.ToUInt32(hash, 0) & 0x7FFFFFFF);
        return id == 0 ? 1 : id;
    }

    private static string NormalizeBrand(string brand) => brand.Trim().ToUpperInvariant();

    /// <summary>
    /// <c>color_hex</c> for one colour, or <c>multi_color_hexes</c> (comma-separated, coaxial) for
    /// several. Spoolman forbids both at once. Upper-case, no <c>#</c>, 6 or 8 hex digits, as
    /// Spoolman sanitizes; anything else is dropped rather than failing the list.
    /// </summary>
    internal static (string? ColorHex, string? MultiColorHexes) Colors(SourceRow row)
    {
        var colors = (row.Colors ?? [])
            .Select(SanitizeHex)
            .OfType<string>()
            .ToList();
        if (colors.Count >= 2)
        {
            return (null, string.Join(",", colors));
        }

        return (colors.FirstOrDefault() ?? SanitizeHex(row.ColorHex), null);
    }

    private static string? SanitizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = value.Trim().TrimStart('#').ToUpperInvariant();
        return hex.Length is 6 or 8 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary><c>PurchasePriceValue</c> is free text; a price is reported only when it is a non-negative number.</summary>
    internal static double? Price(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimStart('$', '€', '£', '¥').Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
            && double.IsFinite(price) && price >= 0
            ? price
            : null;
    }

    private static double? Grams(long? milligrams) => milligrams.HasValue ? milligrams.Value / 1000.0 : null;

    private static int? Temperature(double? celsius) =>
        celsius is { } value && double.IsFinite(value) && value >= 0 ? (int)Math.Round(value) : null;

    /// <summary>Spoolman's <c>length_from_weight</c>: grams to millimetres of filament.</summary>
    internal static double LengthMm(double grams, double density, double diameterMm)
    {
        var areaMm2 = Math.PI * (diameterMm / 2) * (diameterMm / 2);
        // grams / (g/cm3) = cm3; * 1000 = mm3; / mm2 = mm.
        return grams / density * 1000.0 / areaMm2;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>What the inventory query reads per spool.</summary>
    internal sealed class SourceRow
    {
        public Guid Id { get; init; }
        public int SpoolNumber { get; init; }
        public string? DisplayName { get; init; }
        public string? Brand { get; init; }
        public string? MaterialType { get; init; }
        public double Density { get; init; }
        public double? DiameterMm { get; init; }
        public string? ColorHex { get; init; }
        public List<string>? Colors { get; init; }
        public double? RecommendedTemp { get; init; }
        public double? RecommendedBedTemp { get; init; }
        public bool IsActive { get; init; }
        public string? StorageLocation { get; init; }
        public string? Notes { get; init; }
        public string? PurchasePriceValue { get; init; }
        public DateTime CreatedDate { get; init; }
        public long? InitialNominalWeightMg { get; init; }
        public long? SpoolWeightMg { get; init; }
        public long? RemainingMg { get; init; }
        public long UsedMg { get; init; }
    }

    private static System.Linq.Expressions.Expression Replace(
        System.Linq.Expressions.Expression body, System.Linq.Expressions.Expression from, System.Linq.Expressions.Expression to) =>
        new ParameterReplacer(from, to).Visit(body)
        ?? throw new InvalidOperationException("Rewriting an expression produced nothing.");

    private sealed class ParameterReplacer(System.Linq.Expressions.Expression from, System.Linq.Expressions.Expression to) : System.Linq.Expressions.ExpressionVisitor
    {
        public override System.Linq.Expressions.Expression? Visit(System.Linq.Expressions.Expression? node) =>
            node == from ? to : base.Visit(node);
    }
}
