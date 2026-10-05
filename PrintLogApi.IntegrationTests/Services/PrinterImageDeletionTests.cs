using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Deleting a printer, and deleting an account, must take the printer images with them.
/// Both paths hit the PrinterImage -&gt; File Restrict FKs, so getting the order wrong fails
/// loudly rather than orphaning quietly - which is why neither can be left uncovered.
/// </summary>
public class PrinterImageDeletionTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    private static MemoryStream MakePng()
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return new MemoryStream(data.ToArray()) { Position = 0 };
    }

    private static async Task<Printer> CreatePrinterAsync(
        PrintLogContext context, long userId, CancellationToken ct)
    {
        var printer = new Printer
        {
            Name = $"Deletion Printer {Guid.NewGuid()}",
            Make = "Prusa",
            Model = "MK4",
            UserId = userId,
            IsActive = true
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(ct);
        return printer;
    }

    [Fact]
    public async Task DeletePrinter_RemovesImageRowsFilesAndBlobs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var imageService = scope.ServiceProvider.GetRequiredService<IPrinterImageService>();
        var printerService = scope.ServiceProvider.GetRequiredService<IPrinterService>();
        var blobs = (InMemoryBlobStorageService)scope.ServiceProvider.GetRequiredService<IBlobStorageService>();

        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);

        var printer = await CreatePrinterAsync(context, userId, ct);
        using var png = MakePng();
        var image = await imageService.AddImageAsync(printer.Id, png, userId, ct);
        var fileId = image.FileId;
        var deletesBefore = blobs.DeletedBlobNames.Count;

        await printerService.DeletePrinter(printer.Id);

        Assert.False(await context.PrinterImages.AnyAsync(pi => pi.PrinterId == printer.Id, ct));
        Assert.False(await context.Files.AnyAsync(f => f.Id == fileId, ct));
        Assert.False(await context.Printers.AnyAsync(p => p.Id == printer.Id, ct));
        Assert.True(blobs.DeletedBlobNames.Count > deletesBefore);
    }

    [Fact]
    public async Task DeleteAllDataForUser_RemovesPrinterImagesAndTheirBlobs()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var imageService = scope.ServiceProvider.GetRequiredService<IPrinterImageService>();
        var deletionService = scope.ServiceProvider.GetRequiredService<IUserDeletionService>();
        var blobs = (InMemoryBlobStorageService)scope.ServiceProvider.GetRequiredService<IBlobStorageService>();

        // A user of its own: this test destroys the account it runs against.
        var user = new User
        {
            OAuthUserId = $"auth0|printer-image-deletion-{Guid.NewGuid()}",
            DisplayName = "Deletion Victim"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        var printer = await CreatePrinterAsync(context, user.Id, ct);
        using var png = MakePng();
        var image = await imageService.AddImageAsync(printer.Id, png, user.Id, ct);
        var fileId = image.FileId;
        var deletesBefore = blobs.DeletedBlobNames.Count;

        // The account-deletion request arrives in its own scope with an empty change tracker.
        // Leaving the entities this test just wrote tracked would make EF resolve fixups that
        // production never sees, which is why UserDeletionServiceTests clears too.
        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, ct);

        await deletionService.DeleteAllDataForUser(userToDelete);

        Assert.False(await context.PrinterImages.AnyAsync(pi => pi.PrinterId == printer.Id, ct));
        Assert.False(await context.Files.AnyAsync(f => f.Id == fileId, ct));
        Assert.True(blobs.DeletedBlobNames.Count > deletesBefore);
    }
}
