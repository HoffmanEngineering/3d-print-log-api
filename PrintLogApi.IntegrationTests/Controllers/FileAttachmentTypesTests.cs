using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Which files a Pro user may attach to a print. Besides model files, an MP4 video: the timelapse
/// moonraker-timelapse renders of a print, which printlog-bridge attaches (printlog-bridge#42).
///
/// Each test arranges a fresh Pro user and print, so the seeded (free) test user stays free for
/// the 403 tests in <see cref="PrintsControllerTests"/>.
/// </summary>
public class FileAttachmentTypesTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("timelapse_benchy.gcode_2026-10-15_12-00-00.mp4")]
    [InlineData("TIMELAPSE.MP4")]
    public async Task GetFileUploadUrl_AcceptsAnMp4Video(string fileName)
    {
        var (user, printId) = await ArrangeProPrintAsync();

        var response = await PostAsync(user, $"/api/Prints/{printId}/files/upload-url", new GetUploadUrlRequest
        {
            FileName = fileName,
            ContentType = "video/mp4",
            SizeBytes = 50L * 1024 * 1024,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upload = (await response.Content.ReadFromJsonAsync<GetUploadUrlResponse>(cancellationToken: Ct))!;
        Assert.StartsWith($"printattachments/{printId}/", upload.BlobPath);
        Assert.EndsWith(".mp4", upload.BlobPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(upload.SasUrl));
    }

    [Fact]
    public async Task ConfirmFileUpload_AttachesTheVideo()
    {
        var (user, printId) = await ArrangeProPrintAsync();
        const string fileName = "timelapse_benchy.gcode_2026-10-15_12-00-00.mp4";
        const long size = 12_345_678;
        var urlResponse = await PostAsync(user, $"/api/Prints/{printId}/files/upload-url", new GetUploadUrlRequest
        {
            FileName = fileName,
            ContentType = "video/mp4",
            SizeBytes = size,
        });
        var upload = (await urlResponse.Content.ReadFromJsonAsync<GetUploadUrlResponse>(cancellationToken: Ct))!;

        var confirm = await PostAsync(user, $"/api/Prints/{printId}/files/confirm", new ConfirmUploadRequest
        {
            BlobPath = upload.BlobPath,
            FileName = fileName,
            ContentType = "video/mp4",
            SizeBytes = size,
        });

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var files = (await _client.GetFromJsonAsync<List<PrintAttachmentDto>>($"/api/Prints/{printId}/files", Ct))!;
        var attached = Assert.Single(files);
        Assert.Equal(fileName, attached.OriginalFileName);
        Assert.Equal("video/mp4", attached.ContentType);
        Assert.Equal(size, attached.SizeBytes);
    }

    [Theory]
    [InlineData("benchy.exe")]
    [InlineData("timelapse.mkv")]
    [InlineData("timelapse.mov")]
    [InlineData("timelapse")]
    public async Task GetFileUploadUrl_StillRefusesOtherTypes(string fileName)
    {
        var (user, printId) = await ArrangeProPrintAsync();

        var response = await PostAsync(user, $"/api/Prints/{printId}/files/upload-url", new GetUploadUrlRequest
        {
            FileName = fileName,
            ContentType = "application/octet-stream",
            SizeBytes = 1024,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostAsync(User user, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return _client.SendAsync(request, Ct);
    }

    /// <summary>A fresh Pro user with a public print of their own.</summary>
    private async Task<(User User, long PrintId)> ArrangeProPrintAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|attachments-{Guid.NewGuid():N}",
            DisplayName = "Attachments",
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
