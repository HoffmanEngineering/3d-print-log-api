using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>Seeding helpers shared by the popularity and backfill tests.</summary>
internal static class PrinterCatalogTestData
{
    public static CatalogModel Model(string id, string make, string model, IReadOnlyList<string>? aliases = null, int? featured = null) => new()
    {
        Id = id,
        SourceKeys = [$"orca:{id}"],
        Make = make,
        Model = model,
        Featured = featured,
        Aliases = aliases,
        Technology = "FFF",
        BedWidthMm = 256,
        BedDepthMm = 256,
        BedHeightMm = 250,
        NozzleOptionsMm = [0.4],
        DefaultNozzleMm = 0.4,
        FilamentDiameterMm = 1.75,
        HasHeatedBed = true,
        ExtruderCount = 1,
        Sources = new Dictionary<string, string> { ["bedWidthMm"] = "orca" },
    };

    public static CatalogFile File(params CatalogModel[] models) => new() { Version = "v1", Models = models, Reserved = [] };

    /// <summary>Every column of a row as a name-to-value map, navigations left out, for whole-row comparisons.</summary>
    public static SortedDictionary<string, object?> Columns(object row) => new(row.GetType().GetProperties()
        .Where(p => p.GetIndexParameters().Length == 0
            && (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) is { } t
            && (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTimeOffset) || t == typeof(DateTime)))
        .ToDictionary(p => p.Name, p => p.GetValue(row)));

    public static async Task<User> AddUserAsync(PrinterCatalogTestHost host)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User { OAuthUserId = $"catalog-{Guid.NewGuid():N}" };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    public static async Task<long> AddPrinterAsync(
        PrinterCatalogTestHost host, long userId, string? catalogModelId, DateTimeOffset? createdAt,
        bool active = true, string? make = null, string? model = null, params DateTimeOffset[] printStarts)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer
        {
            UserId = userId,
            Name = "p",
            Make = make,
            Model = model,
            IsActive = active,
            CatalogModelId = catalogModelId,
            CreatedAt = createdAt,
            Description = "keep me",
            NozzleDiameter = 0.6,
            SlotCount = 4,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        foreach (var start in printStarts)
        {
            db.Prints.Add(new Print { PrinterId = printer.Id, StartDate = start, Title = "t", CreatedById = userId, UpdatedById = userId });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return printer.Id;
    }

    public static async Task<Printer> GetPrinterAsync(PrinterCatalogTestHost host, long id)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>()
            .Printers.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
    }
}
