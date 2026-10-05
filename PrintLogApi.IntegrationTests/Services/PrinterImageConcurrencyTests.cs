using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The two default-flag invariants the design names.
/// </summary>
/// <remarks>
/// NOTE: these run on SQLite. They demonstrate the LOGIC - that the demote-and-retry and the
/// read-side fallback exist and work - not SQL Server isolation behavior. Reading those
/// queries by hand is still required; a green run here does not prove the production database
/// serializes anything.
/// </remarks>
public class PrinterImageConcurrencyTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    private static MemoryStream MakePng()
    {
        using var bitmap = new SKBitmap(80, 80);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return new MemoryStream(data.ToArray()) { Position = 0 };
    }

    private async Task<(long PrinterId, long UserId)> CreatePrinterAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);

        var printer = new Printer
        {
            Name = $"Concurrency Printer {Guid.NewGuid()}",
            Make = "Prusa",
            Model = "MK4",
            UserId = userId,
            IsActive = true
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(ct);
        return (printer.Id, userId);
    }

    /// <summary>
    /// Each concurrent caller needs its own scope: a DbContext is not thread-safe, and
    /// sharing one would exercise EF's concurrency guard rather than the database's.
    /// </summary>
    private async Task AddImageInOwnScopeAsync(long printerId, long userId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterImageService>();
        using var png = MakePng();
        await service.AddImageAsync(printerId, png, userId, ct);
    }

    [Fact]
    public async Task ConcurrentFirstUploads_ProduceExactlyOneDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var (printerId, userId) = await CreatePrinterAsync(ct);

        await Task.WhenAll(
            AddImageInOwnScopeAsync(printerId, userId, ct),
            AddImageInOwnScopeAsync(printerId, userId, ct));

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var defaults = await context.PrinterImages
            .AsNoTracking()
            .CountAsync(pi => pi.PrinterId == printerId && pi.IsDefault, ct);

        // If this fails, the demote-and-retry in PrinterImageService.AddImageAsync is wrong.
        Assert.Equal(1, defaults);
        Assert.Equal(2, await context.PrinterImages.CountAsync(pi => pi.PrinterId == printerId, ct));
    }

    [Fact]
    public async Task DeleteInterleavedWithAdd_StillYieldsExactlyOneDefaultOnRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (printerId, userId) = await CreatePrinterAsync(ct);

        int soleId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var service = seedScope.ServiceProvider.GetRequiredService<IPrinterImageService>();
            using var png = MakePng();
            soleId = (await service.AddImageAsync(printerId, png, userId, ct)).Id;
        }

        await Task.WhenAll(
            DeleteInOwnScopeAsync(printerId, soleId, userId, ct),
            AddImageInOwnScopeAsync(printerId, userId, ct));

        using var scope = _factory.Services.CreateScope();
        var printerService = scope.ServiceProvider.GetRequiredService<IPrinterService>();

        var dto = new PrinterDetailDto { Id = printerId };
        await printerService.HydrateDetailImageUrlsAsync(dto, ct);

        // The read-side fallback is what guarantees this, not the index: a filtered unique
        // index can enforce at most one default, never at least one.
        if (dto.Images!.Count > 0)
        {
            Assert.Single(dto.Images!, i => i.IsDefault);
        }
    }

    private async Task DeleteInOwnScopeAsync(long printerId, int imageId, long userId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPrinterImageService>();
        try
        {
            await service.DeleteImageAsync(printerId, imageId, userId, ct);
        }
        catch (PrintLogApi.Exceptions.DoesNotExistException)
        {
            // The interleaving under test: the row may already be gone. What matters is the
            // state the READ side reports afterwards.
        }
    }
}
