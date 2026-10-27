using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Deleting an account must take print and project images with it, thumbnails included. The
/// thumbnail FKs are Restrict, so a delete that misses them fails rather than orphaning quietly.
/// </summary>
public class PrintAndProjectImageDeletionTests(CustomWebApplicationFactory factory)
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

    [Fact]
    public async Task DeleteAllDataForUser_RemovesPrintAndProjectImagesWithThumbnails()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var processing = scope.ServiceProvider.GetRequiredService<IImageProcessingService>();
        var writer = scope.ServiceProvider.GetRequiredService<IProcessedImageWriter>();
        var deletionService = scope.ServiceProvider.GetRequiredService<IUserDeletionService>();
        var blobs = (InMemoryBlobStorageService)scope.ServiceProvider.GetRequiredService<IBlobStorageService>();

        // A user of its own: this test destroys the account it runs against.
        var user = new User
        {
            OAuthUserId = $"auth0|print-image-deletion-{Guid.NewGuid()}",
            DisplayName = "Deletion Victim"
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(ct);

        var printer = new Printer { Name = "Deletion Printer", Make = "Prusa", Model = "MK4", UserId = user.Id, IsActive = true };
        context.Printers.Add(printer);
        await context.SaveChangesAsync(ct);

        var print = new Print
        {
            Title = "Deletion Print",
            PrinterId = printer.Id,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        var project = new Project
        {
            Name = "Deletion Project",
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        context.Prints.Add(print);
        context.Projects.Add(project);
        await context.SaveChangesAsync(ct);

        var uploaded = new List<string>();
        using var printPng = MakePng();
        var printFiles = await writer.WriteAsync(
            BlobContainers.PrintImages, await processing.ProcessAsync(printPng, ct), user.Id, uploaded, ct);
        using var projectPng = MakePng();
        var projectFiles = await writer.WriteAsync(
            BlobContainers.ProjectImages, await processing.ProcessAsync(projectPng, ct), user.Id, uploaded, ct);

        context.PrintImages.Add(new PrintImage
        {
            PrintId = print.Id,
            File = printFiles.Original,
            ThumbnailFile = printFiles.Thumbnail,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        });
        context.ProjectImages.Add(new ProjectImage
        {
            ProjectId = project.Id,
            File = projectFiles.Original,
            ThumbnailFile = projectFiles.Thumbnail,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        });
        await context.SaveChangesAsync(ct);

        var paths = new[] { printFiles.Original, printFiles.Thumbnail!, projectFiles.Original, projectFiles.Thumbnail! }
            .Select(f => f.Path!)
            .ToList();
        var fileIds = new[] { printFiles.Original, printFiles.Thumbnail!, projectFiles.Original, projectFiles.Thumbnail! }
            .Select(f => f.Id)
            .ToList();
        Assert.All(paths, path => Assert.True(blobs.BlobExists(path)));

        // The account-deletion request arrives in its own scope with an empty change tracker.
        context.ChangeTracker.Clear();
        var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id, ct);

        await deletionService.DeleteAllDataForUser(userToDelete);

        Assert.False(await context.Files.AnyAsync(f => fileIds.Contains(f.Id), ct));
        Assert.All(paths, path => Assert.False(blobs.BlobExists(path), path));
    }
}
