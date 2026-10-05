using Microsoft.EntityFrameworkCore;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class PrinterImageServiceTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    private static MemoryStream MakePng(int width = 100, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return new MemoryStream(data.ToArray()) { Position = 0 };
    }

    private static MemoryStream NotAnImage()
        => new("this is not an image at all"u8.ToArray()) { Position = 0 };

    /// <summary>
    /// Reads the user from THIS factory database rather than the seeder statics: xUnit runs
    /// test classes in parallel, each with its own factory, and the static holds whichever
    /// one seeded last.
    /// </summary>
    private static Task<long> UserIdAsync(PrintLogContext context, string oauthId, CancellationToken ct)
        => context.Users.Where(u => u.OAuthUserId == oauthId).Select(u => u.Id).FirstAsync(ct);

    private static async Task<Printer> CreatePrinterAsync(
        PrintLogContext context, long userId, CancellationToken ct)
    {
        var printer = new Printer
        {
            Name = $"Image Test Printer {Guid.NewGuid()}",
            Make = "Prusa",
            Model = "MK4",
            UserId = userId,
            IsActive = true
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(ct);
        return printer;
    }

    private sealed record Fixture(
        IServiceScope Scope,
        PrintLogContext Context,
        IPrinterImageService Service,
        InMemoryBlobStorageService Blobs,
        Printer Printer,
        long UserId);

    private async Task<Fixture> ArrangeAsync(CancellationToken ct)
    {
        var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var userId = await UserIdAsync(context, IntegrationTestSeeder.TestUserOAuthId, ct);
        var printer = await CreatePrinterAsync(context, userId, ct);

        return new Fixture(
            scope,
            context,
            scope.ServiceProvider.GetRequiredService<IPrinterImageService>(),
            (InMemoryBlobStorageService)scope.ServiceProvider.GetRequiredService<IBlobStorageService>(),
            printer,
            userId);
    }

    // -- Add ---------------------------------------------------------------------------

    [Fact]
    public async Task AddImage_ForeignPrinter_ThrowsDoesNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        var otherId = await UserIdAsync(f.Context, IntegrationTestSeeder.SecondaryUserOAuthId, ct);
        var foreign = await CreatePrinterAsync(f.Context, otherId, ct);

        using var png = MakePng();
        await Assert.ThrowsAsync<DoesNotExistException>(() =>
            f.Service.AddImageAsync(foreign.Id, png, f.UserId, ct));
    }

    [Fact]
    public async Task AddImage_FirstImage_BecomesDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var png = MakePng();
        var image = await f.Service.AddImageAsync(f.Printer.Id, png, f.UserId, ct);

        Assert.True(image.IsDefault);
        Assert.Equal(0, image.DisplayOrder);
    }

    [Fact]
    public async Task AddImage_SecondImage_IsNotDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var first = MakePng();
        await f.Service.AddImageAsync(f.Printer.Id, first, f.UserId, ct);
        using var second = MakePng();
        var image = await f.Service.AddImageAsync(f.Printer.Id, second, f.UserId, ct);

        Assert.False(image.IsDefault);
        Assert.Equal(1, image.DisplayOrder);
    }

    [Fact]
    public async Task AddImage_OverCap_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        for (var i = 0; i < SubscriptionLimits.FreeMaxImages; i++)
        {
            using var png = MakePng();
            await f.Service.AddImageAsync(f.Printer.Id, png, f.UserId, ct);
        }

        using var overflow = MakePng();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            f.Service.AddImageAsync(f.Printer.Id, overflow, f.UserId, ct));
    }

    [Fact]
    public async Task AddImage_InvalidBytes_StoresNoBlobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        var uploadsBefore = f.Blobs.UploadedBlobNames.Count;

        using var junk = NotAnImage();
        await Assert.ThrowsAsync<InvalidImageException>(() =>
            f.Service.AddImageAsync(f.Printer.Id, junk, f.UserId, ct));

        // The decode runs before any storage call, so a rejected upload leaves nothing behind.
        Assert.Equal(uploadsBefore, f.Blobs.UploadedBlobNames.Count);
    }

    // -- Delete ------------------------------------------------------------------------

    [Fact]
    public async Task DeleteImage_ForeignPrinter_ThrowsDoesNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        var otherId = await UserIdAsync(f.Context, IntegrationTestSeeder.SecondaryUserOAuthId, ct);
        var foreign = await CreatePrinterAsync(f.Context, otherId, ct);
        using var png = MakePng();
        var image = await f.Service.AddImageAsync(foreign.Id, png, otherId, ct);

        await Assert.ThrowsAsync<DoesNotExistException>(() =>
            f.Service.DeleteImageAsync(foreign.Id, image.Id, f.UserId, ct));
    }

    [Fact]
    public async Task DeleteImage_UploadedByCaller_ButForeignPrinter_StillThrows()
    {
        // Defeats a pi.CreatedById predicate, which compiles, reads plausibly, and would
        // wrongly allow this. The plain foreign-printer test above cannot catch it, because
        // there the uploader and the owner are the same person.
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        var otherId = await UserIdAsync(f.Context, IntegrationTestSeeder.SecondaryUserOAuthId, ct);
        var foreign = await CreatePrinterAsync(f.Context, otherId, ct);
        using var png = MakePng();
        var image = await f.Service.AddImageAsync(foreign.Id, png, otherId, ct);

        var tracked = await f.Context.PrinterImages.SingleAsync(pi => pi.Id == image.Id, ct);
        tracked.CreatedById = f.UserId;
        await f.Context.SaveChangesAsync(ct);

        await Assert.ThrowsAsync<DoesNotExistException>(() =>
            f.Service.DeleteImageAsync(foreign.Id, image.Id, f.UserId, ct));
    }

    [Fact]
    public async Task DeleteImage_RemovesRowsFilesAndBlobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var png = MakePng();
        var image = await f.Service.AddImageAsync(f.Printer.Id, png, f.UserId, ct);
        var fileId = image.FileId;
        var deletesBefore = f.Blobs.DeletedBlobNames.Count;

        await f.Service.DeleteImageAsync(f.Printer.Id, image.Id, f.UserId, ct);

        Assert.False(await f.Context.PrinterImages.AnyAsync(pi => pi.Id == image.Id, ct));
        Assert.False(await f.Context.Files.AnyAsync(x => x.Id == fileId, ct));
        Assert.True(f.Blobs.DeletedBlobNames.Count > deletesBefore);
    }

    [Fact]
    public async Task DeleteImage_OfDefault_PromotesTheNext()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var a = MakePng();
        var first = await f.Service.AddImageAsync(f.Printer.Id, a, f.UserId, ct);
        using var b = MakePng();
        var second = await f.Service.AddImageAsync(f.Printer.Id, b, f.UserId, ct);

        await f.Service.DeleteImageAsync(f.Printer.Id, first.Id, f.UserId, ct);

        var promoted = await f.Context.PrinterImages
            .AsNoTracking().SingleAsync(pi => pi.Id == second.Id, ct);
        Assert.True(promoted.IsDefault);
    }

    // -- Reorder -----------------------------------------------------------------------

    [Fact]
    public async Task Reorder_PartialIdSet_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var a = MakePng();
        var first = await f.Service.AddImageAsync(f.Printer.Id, a, f.UserId, ct);
        using var b = MakePng();
        await f.Service.AddImageAsync(f.Printer.Id, b, f.UserId, ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            f.Service.ReorderImagesAsync(f.Printer.Id, new[] { first.Id }, f.UserId, ct));
    }

    [Fact]
    public async Task Reorder_DuplicateIds_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var a = MakePng();
        var first = await f.Service.AddImageAsync(f.Printer.Id, a, f.UserId, ct);
        using var b = MakePng();
        await f.Service.AddImageAsync(f.Printer.Id, b, f.UserId, ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            f.Service.ReorderImagesAsync(f.Printer.Id, new[] { first.Id, first.Id }, f.UserId, ct));
    }

    [Fact]
    public async Task Reorder_CompleteSet_RewritesDisplayOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var a = MakePng();
        var first = await f.Service.AddImageAsync(f.Printer.Id, a, f.UserId, ct);
        using var b = MakePng();
        var second = await f.Service.AddImageAsync(f.Printer.Id, b, f.UserId, ct);

        await f.Service.ReorderImagesAsync(
            f.Printer.Id, new[] { second.Id, first.Id }, f.UserId, ct);

        var order = await f.Context.PrinterImages
            .AsNoTracking()
            .Where(pi => pi.PrinterId == f.Printer.Id)
            .OrderBy(pi => pi.DisplayOrder)
            .Select(pi => pi.Id)
            .ToListAsync(ct);

        Assert.Equal(new[] { second.Id, first.Id }, order);
    }

    [Fact]
    public async Task Reorder_ForeignPrinter_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        var otherId = await UserIdAsync(f.Context, IntegrationTestSeeder.SecondaryUserOAuthId, ct);
        var foreign = await CreatePrinterAsync(f.Context, otherId, ct);
        using var png = MakePng();
        var image = await f.Service.AddImageAsync(foreign.Id, png, otherId, ct);

        // The owner filter empties the set, so the exact-set check rejects it.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            f.Service.ReorderImagesAsync(foreign.Id, new[] { image.Id }, f.UserId, ct));
    }

    // -- Set default -------------------------------------------------------------------

    [Fact]
    public async Task SetDefault_MovesTheFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        using var a = MakePng();
        await f.Service.AddImageAsync(f.Printer.Id, a, f.UserId, ct);
        using var b = MakePng();
        var second = await f.Service.AddImageAsync(f.Printer.Id, b, f.UserId, ct);

        await f.Service.SetDefaultImageAsync(f.Printer.Id, second.Id, f.UserId, ct);

        var images = await f.Context.PrinterImages
            .AsNoTracking()
            .Where(pi => pi.PrinterId == f.Printer.Id)
            .ToListAsync(ct);

        Assert.Single(images, i => i.IsDefault);
        Assert.True(images.Single(i => i.Id == second.Id).IsDefault);
    }

    [Fact]
    public async Task SetDefault_UploadedByCaller_ButForeignPrinter_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await ArrangeAsync(ct);
        using var scope = f.Scope;

        var otherId = await UserIdAsync(f.Context, IntegrationTestSeeder.SecondaryUserOAuthId, ct);
        var foreign = await CreatePrinterAsync(f.Context, otherId, ct);
        using var png = MakePng();
        var image = await f.Service.AddImageAsync(foreign.Id, png, otherId, ct);

        var tracked = await f.Context.PrinterImages.SingleAsync(pi => pi.Id == image.Id, ct);
        tracked.CreatedById = f.UserId;
        await f.Context.SaveChangesAsync(ct);

        await Assert.ThrowsAsync<DoesNotExistException>(() =>
            f.Service.SetDefaultImageAsync(foreign.Id, image.Id, f.UserId, ct));
    }
}
