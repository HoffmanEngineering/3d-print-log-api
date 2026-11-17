using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using SkiaSharp;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Image DTOs and the summaries that carry a default image id also carry the thumbnail's URL,
/// as an API-relative path the web app prefixes with its API base URL, as it does for image ids.
/// </summary>
public class ThumbnailUrlTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private static ByteArrayContent Png()
    {
        using var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Orange);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    /// <summary>A tag short enough for the 50-character search box, so a query finds only this test's rows.</summary>
    private static string NewTag() => Guid.NewGuid().ToString("N");

    private async Task<(long PrintId, PrintImageDto Image)> CreatePrintWithImageAsync(string tag, Guid? projectId = null)
    {
        var create = AuthenticatedRequest(HttpMethod.Post, "/api/Prints");
        create.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"Print {tag}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Public,
            ProjectId = projectId,
        });
        var created = await _client.SendAsync(create, Ct);
        created.EnsureSuccessStatusCode();
        var printId = (await created.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!.Id;

        var form = new MultipartFormDataContent { { Png(), "image", "a.png" }, { new StringContent("true"), "isDefault" } };
        var upload = AuthenticatedRequest(HttpMethod.Post, $"/api/Prints/{printId}/image");
        upload.Content = form;
        var uploaded = await _client.SendAsync(upload, Ct);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        return (printId, (await uploaded.Content.ReadFromJsonAsync<PrintImageDto>(Ct))!);
    }

    private async Task<(Guid ProjectId, ProjectImageDto Image)> CreateProjectWithImageAsync(string tag)
    {
        var create = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        create.Content = JsonContent.Create(new AddProjectDto
        {
            Name = $"Project {tag}",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Public,
        });
        var created = await _client.SendAsync(create, Ct);
        created.EnsureSuccessStatusCode();
        var projectId = (await created.Content.ReadFromJsonAsync<ProjectDetailDto>(Ct))!.Id;

        var upload = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{projectId}/images");
        upload.Content = new MultipartFormDataContent { { Png(), "file", "p.png" } };
        var uploaded = await _client.SendAsync(upload, Ct);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        return (projectId, (await uploaded.Content.ReadFromJsonAsync<ProjectImageDto>(Ct))!);
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, url), Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task UploadedPrintImage_CarriesItsThumbnailUrl_WhichServesTheThumbnail()
    {
        var (printId, image) = await CreatePrintWithImageAsync(NewTag());

        Assert.Equal($"/api/Prints/{printId}/image/{image.Id}/thumbnail", image.ThumbnailUrl);
        var thumbnail = await _client.GetAsync(image.ThumbnailUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/webp", thumbnail.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PrintDetail_ImagesCarryThumbnailUrls()
    {
        var (printId, image) = await CreatePrintWithImageAsync(NewTag());

        var detail = await GetJsonAsync($"/api/Prints/{printId}");

        var dto = Assert.Single(detail.GetProperty("images").EnumerateArray());
        Assert.Equal(image.Id, dto.GetProperty("id").GetInt32());
        Assert.Equal($"/api/Prints/{printId}/image/{image.Id}/thumbnail", dto.GetProperty("thumbnailUrl").GetString());
    }

    [Fact]
    public async Task PrintSummary_CarriesTheDefaultImagesThumbnailUrl()
    {
        var tag = NewTag();
        var (printId, image) = await CreatePrintWithImageAsync(tag);

        var page = await GetJsonAsync($"/api/Prints/summary?searchText={tag}");

        var item = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == printId);
        Assert.Equal(image.Id, item.GetProperty("defaultPrintImageId").GetInt32());
        Assert.Equal($"/api/Prints/{printId}/image/{image.Id}/thumbnail",
            item.GetProperty("defaultPrintImageThumbnailUrl").GetString());
    }

    [Fact]
    public void Summary_WithoutADefaultImage_HasNoThumbnailUrl()
    {
        Assert.Null(new PrintSummaryDTO { Id = 5, DefaultPrintImageId = 0 }.DefaultPrintImageThumbnailUrl);
        Assert.Null(new PrintFeedSummaryDto { Id = 5, DefaultPrintImageId = 0 }.DefaultPrintImageThumbnailUrl);
        Assert.Null(new ProjectSummaryDto { Id = Guid.NewGuid(), DefaultImageId = 0 }.DefaultImageThumbnailUrl);
        Assert.Null(new GroupedFeedItemDto { ProjectId = Guid.NewGuid(), DefaultProjectImageId = 0 }.DefaultProjectImageThumbnailUrl);
        Assert.Null(new GroupedFeedItemDto { ProjectId = null, DefaultProjectImageId = 3 }.DefaultProjectImageThumbnailUrl);
    }

    [Fact]
    public void FeedSummary_BuildsTheSameUrl()
    {
        Assert.Equal("/api/Prints/7/image/9/thumbnail",
            new PrintFeedSummaryDto { Id = 7, DefaultPrintImageId = 9 }.DefaultPrintImageThumbnailUrl);
    }

    [Fact]
    public async Task ProjectImageDtos_AndProjectSummaries_CarryThumbnailUrls()
    {
        var tag = NewTag();
        var (projectId, image) = await CreateProjectWithImageAsync(tag);
        var expected = $"/api/Projects/{projectId}/images/{image.Id}/thumbnail";

        var detail = await GetJsonAsync($"/api/Projects/{projectId}");
        var list = await GetJsonAsync($"/api/Projects?search={tag}");

        Assert.Equal(expected, image.ThumbnailUrl);
        var dto = Assert.Single(detail.GetProperty("images").EnumerateArray());
        Assert.Equal(expected, dto.GetProperty("thumbnailUrl").GetString());
        var summary = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == projectId);
        Assert.Equal(expected, summary.GetProperty("defaultImageThumbnailUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(expected, Ct)).StatusCode);
    }

    [Fact]
    public async Task GroupedFeed_CarriesThumbnailUrlsForProjectAndPrintRows()
    {
        var tag = NewTag();
        var (projectId, projectImage) = await CreateProjectWithImageAsync(tag);
        await CreatePrintWithImageAsync(tag, projectId); // a project row needs a matching print
        var (printId, printImage) = await CreatePrintWithImageAsync(tag);

        var page = await GetJsonAsync($"/api/Prints/grouped?searchText={tag}");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        var projectRow = items.Single(i => i.GetProperty("type").GetString() == "project"
            && i.GetProperty("projectId").GetGuid() == projectId);
        Assert.Equal($"/api/Projects/{projectId}/images/{projectImage.Id}/thumbnail",
            projectRow.GetProperty("defaultProjectImageThumbnailUrl").GetString());
        var printRow = items.Single(i => i.GetProperty("type").GetString() == "print"
            && i.GetProperty("print").GetProperty("id").GetInt64() == printId);
        Assert.Equal($"/api/Prints/{printId}/image/{printImage.Id}/thumbnail",
            printRow.GetProperty("print").GetProperty("defaultPrintImageThumbnailUrl").GetString());
    }
}
