using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using FileModel = PrintLogApi.Models.File;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// What <c>files/confirm</c> checks before it records an attachment. The client uploads straight
/// to storage with a SAS URL the API cannot bound, so confirm is the one place the API sees the
/// result: it must accept only the path upload-url issued for this print, record the blob's real
/// length, and hold that length to the per-file limit and the storage quota.
/// </summary>
public class FileAttachmentConfirmTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const string Container = "printattachments";

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InMemoryBlobStorageService Blobs
        => (InMemoryBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();

    [Fact]
    public async Task Confirm_RecordsTheBlobsRealLength_NotTheDeclaredOne()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var blobPath = await IssueAndUploadAsync(user, printId, "benchy.gcode", declaredSize: 10, actualSize: 4321);

        var confirm = await ConfirmAsync(user, printId, blobPath, "benchy.gcode", declaredSize: 10);

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var attached = (await confirm.Content.ReadFromJsonAsync<PrintAttachmentDto>(cancellationToken: Ct))!;
        Assert.Equal(4321, attached.SizeBytes);
        Assert.Equal(4321, await StoredSizeAsync(printId));
    }

    [Fact]
    public async Task Confirm_RefusesAPathUnderAnotherPrint()
    {
        var (victim, victimPrintId) = await ArrangeProPrintAsync();
        var victimPath = await IssueAndUploadAsync(victim, victimPrintId, "secret.stl", 100, 100);
        var (attacker, attackerPrintId) = await ArrangeProPrintAsync();

        var confirm = await ConfirmAsync(attacker, attackerPrintId, victimPath, "secret.stl", 100);

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(0, await AttachmentCountAsync(attackerPrintId));
        // Someone else's blob: refusing it must not delete it.
        Assert.True(Blobs.BlobExists(victimPath));
    }

    [Theory]
    [InlineData("{0}/evil.gcode")]
    [InlineData("{0}/../{1}.gcode")]
    [InlineData("{0}/{1}.gcode/x")]
    [InlineData("{0}/{1}.html")]
    [InlineData("{0}/{1}")]
    [InlineData("{0}/")]
    public async Task Confirm_RefusesAPathUploadUrlWouldNeverIssue(string shape)
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var blobPath = $"{Container}/" + string.Format(shape, printId, Guid.NewGuid());
        Blobs.Blobs[blobPath] = new byte[100];

        var confirm = await ConfirmAsync(user, printId, blobPath, "benchy.gcode", 100);

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(0, await AttachmentCountAsync(printId));
    }

    [Fact]
    public async Task Confirm_RefusesABlobThatWasNeverUploaded()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var upload = await IssueAsync(user, printId, "benchy.gcode", 100);

        var confirm = await ConfirmAsync(user, printId, upload.BlobPath!, "benchy.gcode", 100);

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(0, await AttachmentCountAsync(printId));
    }

    [Fact]
    public async Task Confirm_RefusesAndDeletesABlobOverThePerFileLimit()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var blobPath = await IssueAndUploadAsync(user, printId, "huge.stl", 1024, 1024);
        Blobs.ReportedLengths[blobPath] = 300L * 1024 * 1024;

        var confirm = await ConfirmAsync(user, printId, blobPath, "huge.stl", 1024);

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(0, await AttachmentCountAsync(printId));
        Assert.False(Blobs.BlobExists(blobPath));
    }

    [Fact]
    public async Task Confirm_RefusesAndDeletesABlobThatWouldExceedTheQuota()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var blobPath = await IssueAndUploadAsync(user, printId, "model.3mf", 1024, 1024);
        // Fill the account to just under its quota after upload-url ran, so only confirm can
        // catch it: what the user really uploaded (1 MB) no longer fits.
        await SeedAttachmentAsync(user, printId, SubscriptionLimits.ProMaxFileStorageBytes - 1000);
        Blobs.ReportedLengths[blobPath] = 1024 * 1024;

        var confirm = await ConfirmAsync(user, printId, blobPath, "model.3mf", 1024);

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(1, await AttachmentCountAsync(printId));
        Assert.False(Blobs.BlobExists(blobPath));
    }

    [Fact]
    public async Task Confirm_RefusesAFileBeyondThePerPrintCount()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        // Issue every URL first, as a client racing upload-url would, then confirm them all.
        var paths = new List<string>();
        for (var i = 0; i <= SubscriptionLimits.ProMaxFilesPerPrint; i++)
            paths.Add(await IssueAndUploadAsync(user, printId, $"part{i}.stl", 100, 100));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < paths.Count; i++)
            statuses.Add((await ConfirmAsync(user, printId, paths[i], $"part{i}.stl", 100)).StatusCode);

        Assert.All(statuses.Take(SubscriptionLimits.ProMaxFilesPerPrint), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.BadRequest, statuses[^1]);
        Assert.Equal(SubscriptionLimits.ProMaxFilesPerPrint, await AttachmentCountAsync(printId));
        Assert.False(Blobs.BlobExists(paths[^1]));
    }

    [Theory]
    [InlineData("x.html")]
    [InlineData("benchy.exe")]
    [InlineData("benchy")]
    [InlineData("benchy.stl")] // allowed, but not the type upload-url issued this path for
    public async Task Confirm_RefusesAFileNameWhoseTypeDoesNotMatch(string fileName)
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var blobPath = await IssueAndUploadAsync(user, printId, "benchy.gcode", 100, 100);

        var confirm = await ConfirmAsync(user, printId, blobPath, fileName, 100);

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(0, await AttachmentCountAsync(printId));
    }

    [Fact]
    public async Task Confirm_AcceptsAnUpperCaseExtension()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var blobPath = await IssueAndUploadAsync(user, printId, "BENCHY.GCODE", 100, 100);

        var confirm = await ConfirmAsync(user, printId, blobPath, "BENCHY.GCODE", 100);

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    [Fact]
    public async Task Confirm_RefusesTheSamePathTwice()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        var blobPath = await IssueAndUploadAsync(user, printId, "benchy.gcode", 100, 100);

        var first = await ConfirmAsync(user, printId, blobPath, "benchy.gcode", 100);
        var second = await ConfirmAsync(user, printId, blobPath, "benchy.gcode", 100);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(1, await AttachmentCountAsync(printId));
        // The recorded attachment's blob stays.
        Assert.True(Blobs.BlobExists(blobPath));
    }

    // ---- Helpers ----

    private async Task<GetUploadUrlResponse> IssueAsync(User user, long printId, string fileName, long declaredSize)
    {
        var response = await PostAsync(user, $"/api/Prints/{printId}/files/upload-url", new GetUploadUrlRequest
        {
            FileName = fileName,
            ContentType = "application/octet-stream",
            SizeBytes = declaredSize,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GetUploadUrlResponse>(cancellationToken: Ct))!;
    }

    /// <summary>Issues an upload URL, then stands in for the client's PUT to it.</summary>
    private async Task<string> IssueAndUploadAsync(
        User user, long printId, string fileName, long declaredSize, int actualSize)
    {
        var upload = await IssueAsync(user, printId, fileName, declaredSize);
        Blobs.Blobs[upload.BlobPath!] = new byte[actualSize];
        return upload.BlobPath!;
    }

    private Task<HttpResponseMessage> ConfirmAsync(
        User user, long printId, string blobPath, string fileName, long declaredSize)
        => PostAsync(user, $"/api/Prints/{printId}/files/confirm", new ConfirmUploadRequest
        {
            BlobPath = blobPath,
            FileName = fileName,
            ContentType = "application/octet-stream",
            SizeBytes = declaredSize,
        });

    private Task<HttpResponseMessage> PostAsync(User user, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return _client.SendAsync(request, Ct);
    }

    private async Task<int> AttachmentCountAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrintAttachments.CountAsync(pa => pa.PrintId == printId, Ct);
    }

    private async Task<long> StoredSizeAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrintAttachments.Where(pa => pa.PrintId == printId).Select(pa => pa.File.Size).SingleAsync(Ct);
    }

    private async Task SeedAttachmentAsync(User user, long printId, long size)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var file = new FileModel
        {
            Id = Guid.NewGuid(),
            Path = $"{Container}/{printId}/{Guid.NewGuid()}.stl",
            Size = size,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        db.Files.Add(file);
        db.PrintAttachments.Add(new PrintAttachment
        {
            PrintId = printId,
            FileId = file.Id,
            OriginalFileName = "big.stl",
            ContentType = "application/octet-stream",
            CreatedById = user.Id,
            UpdatedById = user.Id,
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A fresh Pro user with a print of their own.</summary>
    private async Task<(User User, long PrintId)> ArrangeProPrintAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|confirm-{Guid.NewGuid():N}",
            DisplayName = "Confirm",
            ViewStatus = User.ProfileViewStatus.Private,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        db.Subscriptions.Add(new Subscription
        {
            UserId = user.Id,
            Plan = SubscriptionPlan.ProMonthly,
            Status = SubscriptionStatus.Active,
            StripeCustomerId = $"cus_{user.Id}",
            StripeSubscriptionId = $"sub_{user.Id}",
            CreatedById = user.Id,
            UpdatedById = user.Id,
        });
        var printer = new Printer
        {
            Name = $"Printer {Guid.NewGuid():N}"[..20],
            UserId = user.Id,
            IsActive = true,
            CategoryNickname = "FDM",
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(Ct);

        var print = new Print { Title = "Benchy", PrinterId = printer.Id, CreatedById = user.Id, UpdatedById = user.Id };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return (user, print.Id);
    }
}
