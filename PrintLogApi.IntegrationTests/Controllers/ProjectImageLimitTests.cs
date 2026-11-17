using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Project images share the per-entity image cap prints, printers and filaments have
/// (<see cref="SubscriptionLimits.FreeMaxImages"/> / <see cref="SubscriptionLimits.ProMaxImages"/>).
/// A refused upload writes neither a row nor a blob, and a project already past the cap keeps
/// every image it has.
/// </summary>
public class ProjectImageLimitTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record TestUser(long Id, string OAuthId);

    private InMemoryBlobStorageService Blobs()
        => (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();

    private static byte[] PngBytes()
    {
        using var bitmap = new SKBitmap(40, 20);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, TestUser user)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthId);
        return request;
    }

    private async Task<TestUser> CreateUserAsync(SubscriptionStatus? status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|project-image-limit-{Guid.NewGuid():N}",
            DisplayName = "Project image limit",
            ViewStatus = User.ProfileViewStatus.Private,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        if (status is { } s)
        {
            db.Subscriptions.Add(new Subscription
            {
                UserId = user.Id,
                Status = s,
                Plan = s == SubscriptionStatus.Active ? SubscriptionPlan.ProMonthly : SubscriptionPlan.Free,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
            await db.SaveChangesAsync(Ct);
        }
        return new TestUser(user.Id, user.OAuthUserId);
    }

    private async Task SetSubscriptionStatusAsync(TestUser user, SubscriptionStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var subscription = await db.Subscriptions.SingleAsync(s => s.UserId == user.Id, Ct);
        subscription.Status = status;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> CreateProjectAsync(TestUser user)
    {
        var request = Request(HttpMethod.Post, "/api/Projects", user);
        request.Content = JsonContent.Create(new AddProjectDto
        {
            Name = $"Image Limit Project {Guid.NewGuid()}",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private,
        });
        var response = await _client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProjectDetailDto>(Ct);
        return project!.Id;
    }

    private async Task<HttpResponseMessage> UploadAsync(Guid projectId, TestUser user)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(PngBytes());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "project.png");
        var request = Request(HttpMethod.Post, $"/api/Projects/{projectId}/images", user);
        request.Content = form;
        return await _client.SendAsync(request, Ct);
    }

    private async Task UploadSuccessfullyAsync(Guid projectId, TestUser user, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await UploadAsync(projectId, user);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    private async Task<List<int>> ImageIdsAsync(Guid projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.ProjectImages
            .Where(pi => pi.ProjectId == projectId)
            .OrderBy(pi => pi.DisplayOrder)
            .Select(pi => pi.Id)
            .ToListAsync(Ct);
    }

    /// <summary>
    /// Adds image rows straight to the database, the way a project could hold them before the
    /// cap existed. Each row gets a File so it is a complete image.
    /// </summary>
    private async Task SeedImagesAsync(Guid projectId, TestUser user, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        for (var i = 0; i < count; i++)
        {
            var file = new Models.File
            {
                Path = $"{Guid.NewGuid()}.png",
                Size = 1,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
            db.Files.Add(file);
            db.ProjectImages.Add(new ProjectImage
            {
                ProjectId = projectId,
                File = file,
                ContentType = "image/png",
                IsDefault = i == 0,
                DisplayOrder = i,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
        }
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Uploads one image past the cap and asserts it changed nothing.</summary>
    private async Task AssertUploadRefusedAsync(Guid projectId, TestUser user, int expectedMax)
    {
        var rowsBefore = (await ImageIdsAsync(projectId)).Count;
        var blobsBefore = Blobs().UploadedBlobNames.Count;

        var response = await UploadAsync(projectId, user);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            $"Maximum of {expectedMax} images per project allowed",
            await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(rowsBefore, (await ImageIdsAsync(projectId)).Count);
        Assert.Equal(blobsBefore, Blobs().UploadedBlobNames.Count);
    }

    [Fact]
    public async Task FreeUser_FifthImageSucceeds_SixthIsRefusedWithNoRowOrBlob()
    {
        var user = await CreateUserAsync(status: null);
        var projectId = await CreateProjectAsync(user);

        await UploadSuccessfullyAsync(projectId, user, SubscriptionLimits.FreeMaxImages);

        await AssertUploadRefusedAsync(projectId, user, SubscriptionLimits.FreeMaxImages);
        Assert.Equal(SubscriptionLimits.FreeMaxImages, (await ImageIdsAsync(projectId)).Count);
    }

    [Fact]
    public async Task CanceledSubscription_GetsTheFreeLimit()
    {
        var user = await CreateUserAsync(SubscriptionStatus.Canceled);
        var projectId = await CreateProjectAsync(user);

        await UploadSuccessfullyAsync(projectId, user, SubscriptionLimits.FreeMaxImages);

        await AssertUploadRefusedAsync(projectId, user, SubscriptionLimits.FreeMaxImages);
    }

    [Fact]
    public async Task ProUser_TwentiethImageSucceeds_TwentyFirstIsRefusedWithNoRowOrBlob()
    {
        var user = await CreateUserAsync(SubscriptionStatus.Active);
        var projectId = await CreateProjectAsync(user);

        await UploadSuccessfullyAsync(projectId, user, SubscriptionLimits.ProMaxImages);

        await AssertUploadRefusedAsync(projectId, user, SubscriptionLimits.ProMaxImages);
        Assert.Equal(SubscriptionLimits.ProMaxImages, (await ImageIdsAsync(projectId)).Count);
    }

    [Fact]
    public async Task TheCapIsPerProject_AnotherProjectStillAcceptsImages()
    {
        var user = await CreateUserAsync(status: null);
        var full = await CreateProjectAsync(user);
        var other = await CreateProjectAsync(user);
        await UploadSuccessfullyAsync(full, user, SubscriptionLimits.FreeMaxImages);

        var response = await UploadAsync(other, user);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ProjectAlreadyOverTheLimit_KeepsAndManagesItsImages_AndAcceptsUploadsOnceBelowIt()
    {
        var user = await CreateUserAsync(status: null);
        var projectId = await CreateProjectAsync(user);
        const int seeded = SubscriptionLimits.FreeMaxImages + 2;
        await SeedImagesAsync(projectId, user, seeded);

        // Every image is still read.
        var detail = await _client.SendAsync(Request(HttpMethod.Get, $"/api/Projects/{projectId}", user), Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var dto = await detail.Content.ReadFromJsonAsync<ProjectDetailDto>(Ct);
        Assert.Equal(seeded, dto!.Images!.Count);

        // Reorder and set-cover keep working.
        var ids = await ImageIdsAsync(projectId);
        var reversed = Enumerable.Reverse(ids).ToList();
        var reorder = Request(HttpMethod.Put, $"/api/Projects/{projectId}/images/reorder", user);
        reorder.Content = JsonContent.Create(reversed);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(reorder, Ct)).StatusCode);
        Assert.Equal(reversed, await ImageIdsAsync(projectId));

        var setCover = await _client.SendAsync(
            Request(HttpMethod.Post, $"/api/Projects/{projectId}/images/{ids[^1]}/set-as-default", user), Ct);
        Assert.Equal(HttpStatusCode.OK, setCover.StatusCode);

        // Uploads are refused while at or over the cap.
        await AssertUploadRefusedAsync(projectId, user, SubscriptionLimits.FreeMaxImages);

        // Deleting down to the cap still refuses; one below it accepts again.
        var toDelete = seeded - SubscriptionLimits.FreeMaxImages;
        foreach (var imageId in ids.Take(toDelete))
        {
            var delete = await _client.SendAsync(
                Request(HttpMethod.Delete, $"/api/Projects/{projectId}/images/{imageId}", user), Ct);
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }
        Assert.Equal(SubscriptionLimits.FreeMaxImages, (await ImageIdsAsync(projectId)).Count);
        await AssertUploadRefusedAsync(projectId, user, SubscriptionLimits.FreeMaxImages);

        var oneMore = await _client.SendAsync(
            Request(HttpMethod.Delete, $"/api/Projects/{projectId}/images/{ids[toDelete]}", user), Ct);
        Assert.Equal(HttpStatusCode.OK, oneMore.StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(projectId, user)).StatusCode);
        Assert.Equal(SubscriptionLimits.FreeMaxImages, (await ImageIdsAsync(projectId)).Count);
    }

    [Fact]
    public async Task ProUserWhoDowngrades_KeepsTwelveImages_ButCannotAddMore()
    {
        var user = await CreateUserAsync(SubscriptionStatus.Active);
        var projectId = await CreateProjectAsync(user);
        const int uploaded = 12;
        await UploadSuccessfullyAsync(projectId, user, uploaded);

        await SetSubscriptionStatusAsync(user, SubscriptionStatus.Canceled);

        await AssertUploadRefusedAsync(projectId, user, SubscriptionLimits.FreeMaxImages);

        var detail = await _client.SendAsync(Request(HttpMethod.Get, $"/api/Projects/{projectId}", user), Ct);
        var dto = await detail.Content.ReadFromJsonAsync<ProjectDetailDto>(Ct);
        Assert.Equal(uploaded, dto!.Images!.Count);

        var image = await _client.SendAsync(
            Request(HttpMethod.Get, $"/api/Projects/{projectId}/images/{dto.Images[^1].Id}", user), Ct);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
    }
}
