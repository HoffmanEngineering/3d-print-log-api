using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Subscription;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The spec requires ONE definition of storage usage. Three existed before this service:
/// filament upload summed attachments + filament images, attachment upload summed
/// attachments only, and reported usage summed attachments + filament images. A user held
/// to a number they were never shown is the bug these tests exist to prevent.
/// </summary>
public class MediaStorageQuotaTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    private static MemoryStream MakePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return new MemoryStream(data.ToArray()) { Position = 0 };
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private static async Task<Printer> CreatePrinterAsync(PrintLogContext context, long userId, CancellationToken ct)
    {
        var printer = new Printer
        {
            Name = $"Quota Printer {Guid.NewGuid()}",
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
    public async Task GetUsedBytes_CountsPrinterImages()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var quota = scope.ServiceProvider.GetRequiredService<IMediaStorageQuotaService>();
        var images = scope.ServiceProvider.GetRequiredService<IPrinterImageService>();

        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id)
            .FirstAsync(ct);

        var before = await quota.GetUsedBytesAsync(userId, ct);

        var printer = await CreatePrinterAsync(context, userId, ct);
        using var png = MakePng(120, 120);
        var image = await images.AddImageAsync(printer.Id, png, userId, ct);

        var stored = await context.PrinterImages
            .AsNoTracking()
            .Where(pi => pi.Id == image.Id)
            .Select(pi => pi.File.Size + (pi.ThumbnailFile != null ? pi.ThumbnailFile.Size : 0L))
            .SingleAsync(ct);

        Assert.Equal(before + stored, await quota.GetUsedBytesAsync(userId, ct));
    }

    [Fact]
    public async Task GetUsedBytes_CountsForeignUploadsOntoTheOwnerNotTheUploader()
    {
        // CreatedById records the uploader. Charging the uploader would let a user park
        // bytes on someone else's ledger.
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var quota = scope.ServiceProvider.GetRequiredService<IMediaStorageQuotaService>();
        var images = scope.ServiceProvider.GetRequiredService<IPrinterImageService>();

        var owner = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);
        var other = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.SecondaryUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);

        var printer = await CreatePrinterAsync(context, owner, ct);
        using var png = MakePng(120, 120);
        var image = await images.AddImageAsync(printer.Id, png, owner, ct);

        var otherBefore = await quota.GetUsedBytesAsync(other, ct);

        var tracked = await context.PrinterImages.SingleAsync(pi => pi.Id == image.Id, ct);
        tracked.CreatedById = other;
        await context.SaveChangesAsync(ct);

        Assert.Equal(otherBefore, await quota.GetUsedBytesAsync(other, ct));
    }

    [Fact]
    public async Task ReportedUsage_MatchesEnforcedUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var quota = scope.ServiceProvider.GetRequiredService<IMediaStorageQuotaService>();
        var images = scope.ServiceProvider.GetRequiredService<IPrinterImageService>();

        var userId = await context.Users
            .Where(u => u.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId)
            .Select(u => u.Id).FirstAsync(ct);

        var printer = await CreatePrinterAsync(context, userId, ct);
        using var png = MakePng(120, 120);
        await images.AddImageAsync(printer.Id, png, userId, ct);

        var client = _factory.CreateClient();
        var resp = await client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/api/Subscription/me"), ct);
        resp.EnsureSuccessStatusCode();
        var dto = (await resp.Content.ReadFromJsonAsync<SubscriptionDto>(ct))!;

        Assert.Equal(await quota.GetUsedBytesAsync(userId, ct), dto.UsedFileStorageBytes);
    }
}
