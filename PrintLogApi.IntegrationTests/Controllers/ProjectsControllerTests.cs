using System.Net;
using System.Net.Http.Headers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.IntegrationTests.Support;
using Xunit;
using static PrintLogApi.IntegrationTests.Support.ProjectDateSeedHelpers;

namespace PrintLogApi.IntegrationTests.Controllers;

public class ProjectsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public ProjectsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private async Task<ProjectDetailDto> CreateProject(string name)
    {
        var req = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        req.Content = JsonContent.Create(new AddProjectDto
        {
            Name = name,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private
        });
        var resp = await _client.SendAsync(req);
        return (await resp.Content.ReadFromJsonAsync<ProjectDetailDto>())!;
    }

    private async Task<int> UploadImage(Guid projectId)
    {
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(imageBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "test.png");

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{projectId}/images");
        req.Content = form;
        var resp = await _client.SendAsync(req);
        var image = (await resp.Content.ReadFromJsonAsync<ProjectImageDto>())!;
        return image.Id;
    }

    [Fact]
    public async Task GetProjects_ReturnsOk_WithPagedList()
    {
        var request = AuthenticatedRequest(HttpMethod.Get, "/api/Projects?PageNumber=1&PageSize=10");
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
    }

    [Fact]
    public async Task PostProject_CreatesProject_ReturnsCreated()
    {
        var dto = new AddProjectDto
        {
            Name = "Test Voron Build",
            Status = Models.Project.ProjectStatus.InProgress,
            ViewStatus = Models.Project.ProjectViewStatus.Private
        };
        var request = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        request.Content = JsonContent.Create(dto);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ProjectDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        Assert.Equal("Test Voron Build", result.Name);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task GetProjectById_ReturnsProject()
    {
        // Create
        var createDto = new AddProjectDto { Name = "Get Test Project", Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private };
        var createReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        createReq.Content = JsonContent.Create(createDto);
        var createResp = await _client.SendAsync(createReq, TestContext.Current.CancellationToken);
        var created = (await createResp.Content.ReadFromJsonAsync<ProjectDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Get
        var getReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{created.Id}");
        var getResp = await _client.SendAsync(getReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var result = (await getResp.Content.ReadFromJsonAsync<ProjectDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Equal(created.Id, result.Id);
    }

    [Fact]
    public async Task PostProjectImage_ReturnsCreated()
    {
        // Create project
        var project = await CreateProject("Image Test Project");

        // Upload image with explicit content type
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(imageBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "test.png");

        var imgReq = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{project.Id}/images");
        imgReq.Content = form;
        var imgResp = await _client.SendAsync(imgReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, imgResp.StatusCode);
    }

    [Fact]
    public async Task PostProjectImage_LocationHeader_PointsToImageEndpoint()
    {
        var project = await CreateProject("Location Header Test");
        var imageId = await UploadImage(project.Id);

        var imgReq = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{project.Id}/images");
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(imageBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(content, "file", "test.png");
        imgReq.Content = form;

        var response = await _client.SendAsync(imgReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var image = (await response.Content.ReadFromJsonAsync<ProjectImageDto>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Contains($"/api/Projects/{project.Id}/images/{image.Id}",
            response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task PostProjectImage_WithUnsupportedFileType_ReturnsBadRequest()
    {
        var project = await CreateProject("File Type Validation Test");

        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // PDF header bytes
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "document.pdf");

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{project.Id}/images");
        req.Content = form;
        var response = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostProjectImage_WithFileTooLarge_ReturnsBadRequest()
    {
        var project = await CreateProject("File Size Validation Test");

        var form = new MultipartFormDataContent();
        var oversizedContent = new ByteArrayContent(new byte[11 * 1024 * 1024]);
        oversizedContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(oversizedContent, "file", "large.png");

        var req = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{project.Id}/images");
        req.Content = form;
        var response = await _client.SendAsync(req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteProjectImage_RemovesBlobFromStorage()
    {
        var blobService = (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();
        var initialBlobCount = blobService.Blobs.Count;

        var project = await CreateProject("Blob Image Deletion Test");
        var imageId = await UploadImage(project.Id);
        Assert.Equal(initialBlobCount + 1, blobService.Blobs.Count);

        var deleteReq = AuthenticatedRequest(HttpMethod.Delete, $"/api/Projects/{project.Id}/images/{imageId}");
        var deleteResp = await _client.SendAsync(deleteReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        Assert.Equal(initialBlobCount, blobService.Blobs.Count);
    }

    [Fact]
    public async Task DeleteProject_RemovesBlobsFromStorage()
    {
        var blobService = (InMemoryBlobStorageService)_factory.Services.GetRequiredService<IBlobStorageService>();
        var initialBlobCount = blobService.Blobs.Count;

        var project = await CreateProject("Blob Project Deletion Test");
        await UploadImage(project.Id);
        Assert.Equal(initialBlobCount + 1, blobService.Blobs.Count);

        var deleteReq = AuthenticatedRequest(HttpMethod.Delete, $"/api/Projects/{project.Id}");
        var deleteResp = await _client.SendAsync(deleteReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        Assert.Equal(initialBlobCount, blobService.Blobs.Count);
    }

    [Fact]
    public async Task GetProjects_Search_ByName_ReturnsMatchingProject()
    {
        var uniqueName = $"NameSearch-{Guid.NewGuid():N}";
        var createReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        createReq.Content = JsonContent.Create(new AddProjectDto
        {
            Name = uniqueName,
            Status = Models.Project.ProjectStatus.InProgress,
            ViewStatus = Models.Project.ProjectViewStatus.Private
        });
        await _client.SendAsync(createReq, TestContext.Current.CancellationToken);

        var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search={uniqueName}&PageNumber=1&PageSize=10");
        var response = await _client.SendAsync(searchReq, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Contains(result.Items, p => p.Name == uniqueName);
    }

    [Fact]
    public async Task GetProjects_Search_ByReference_ReturnsMatchingProject()
    {
        var uniqueRef = $"REF-{Guid.NewGuid():N}";
        var createReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        createReq.Content = JsonContent.Create(new AddProjectDto
        {
            Name = "Search By Reference Test",
            Reference = uniqueRef,
            Status = Models.Project.ProjectStatus.InProgress,
            ViewStatus = Models.Project.ProjectViewStatus.Private
        });
        await _client.SendAsync(createReq, TestContext.Current.CancellationToken);

        var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search={uniqueRef}&PageNumber=1&PageSize=10");
        var response = await _client.SendAsync(searchReq, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Contains(result.Items, p => p.Reference == uniqueRef);
    }

    [Fact]
    public async Task GetProjects_Search_WithNoMatch_ReturnsEmptyList()
    {
        var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search=NOMATCH-{Guid.NewGuid():N}&PageNumber=1&PageSize=10");
        var response = await _client.SendAsync(searchReq, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetProjects_Search_DoesNotReturnNonMatchingProjects()
    {
        var matchTerm = $"MATCH-{Guid.NewGuid():N}";
        var otherName = $"OTHER-{Guid.NewGuid():N}";

        var matchReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        matchReq.Content = JsonContent.Create(new AddProjectDto { Name = matchTerm, Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private });
        await _client.SendAsync(matchReq, TestContext.Current.CancellationToken);

        var otherReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        otherReq.Content = JsonContent.Create(new AddProjectDto { Name = otherName, Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private });
        var otherResp = await _client.SendAsync(otherReq, TestContext.Current.CancellationToken);
        var otherProject = (await otherResp.Content.ReadFromJsonAsync<ProjectDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search={matchTerm}&PageNumber=1&PageSize=100");
        var response = await _client.SendAsync(searchReq, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.Contains(result.Items, p => p.Name == matchTerm);
        Assert.DoesNotContain(result.Items, p => p.Id == otherProject.Id);
    }

    [Fact]
    public async Task DeleteProject_UnlinksPrints_WhenDeletePrintsFalse()
    {
        // Create project
        var createDto = new AddProjectDto { Name = "Delete Test Project", Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private };
        var createReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        createReq.Content = JsonContent.Create(createDto);
        var createResp = await _client.SendAsync(createReq, TestContext.Current.CancellationToken);
        var project = (await createResp.Content.ReadFromJsonAsync<ProjectDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Delete
        var deleteReq = AuthenticatedRequest(HttpMethod.Delete, $"/api/Projects/{project.Id}?deletePrints=false");
        var deleteResp = await _client.SendAsync(deleteReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // Confirm gone
        var getReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{project.Id}");
        var getResp = await _client.SendAsync(getReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    // ---------------------------------------------------------------------
    // Project start / finish dates
    // ---------------------------------------------------------------------

    private static long TestUserId => IntegrationTestSeeder.TestUserId;

    private async Task<ProjectDetailDto> GetProjectDetailAsync(Guid id)
    {
        var req = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{id}");
        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ProjectDetailDto>(
            cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private async Task<PagedList<ProjectSummaryDto>> GetProjectSummariesAsync(string search)
    {
        var req = AuthenticatedRequest(HttpMethod.Get,
            $"/api/Projects?PageNumber=1&PageSize=50&search={Uri.EscapeDataString(search)}");
        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>(
            cancellationToken: TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Builds a PUT body from the project's CURRENT state. Tests must never hand-build a
    /// partial PUT: PUT is a full replace, and omitting a field silently clears it. That is
    /// the exact hazard this feature has to guard against.
    /// </summary>
    private async Task<PutProjectDto> BuildPutDtoAsync(Guid id)
    {
        var current = await GetProjectDetailAsync(id);
        return new PutProjectDto
        {
            Id = current.Id,
            Name = current.Name,
            Reference = current.Reference,
            Description = current.Description,
            Url = current.Url,
            Status = current.Status,
            ViewStatus = current.ViewStatus,
            StartDateOverride = current.StartDateOverride,
            FinishDateOverride = current.FinishDateOverride,
        };
    }

    private async Task<ProjectDetailDto> PutProjectAsync(Guid id, PutProjectDto dto)
    {
        var req = AuthenticatedRequest(HttpMethod.Put, $"/api/Projects/{id}");
        req.Content = JsonContent.Create(dto);
        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ProjectDetailDto>(
            cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private async Task<ProjectDetailDto> PostProjectAsync(AddProjectDto dto)
    {
        var req = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        req.Content = JsonContent.Create(dto);
        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<ProjectDetailDto>(
            cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private async Task MoveEarliestPrintStartAsync(Guid projectId, DateTimeOffset newStart)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = context.Prints
            .Where(p => p.ProjectId == projectId && p.StartDate != null)
            .OrderBy(p => p.Id)
            .First();
        print.StartDate = newStart;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetProjectById_DerivesDatesFromPrints()
    {
        var name = $"derive-{Guid.NewGuid():N}";
        var projectId = await SeedProjectWithPrintsAsync(_factory, TestUserId, name);

        var detail = await GetProjectDetailAsync(projectId);

        Assert.Equal(new DateOnly(2026, 3, 2), detail.StartDate);
        Assert.Equal(new DateOnly(2026, 3, 6), detail.FinishDate);
        Assert.Null(detail.StartDateOverride);
        Assert.Null(detail.FinishDateOverride);
    }

    [Fact]
    public async Task GetProjectById_UndatedPrints_FallsBackToCreatedDate()
    {
        var projectId = await SeedProjectWithUndatedPrintsAsync(
            _factory, TestUserId, $"undated-{Guid.NewGuid():N}");

        var detail = await GetProjectDetailAsync(projectId);

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), detail.StartDate);
        Assert.Null(detail.FinishDate);
    }

    [Fact]
    public async Task PutProject_SetsAndEchoesStartOverride_LeavingFinishAutomatic()
    {
        var projectId = await SeedProjectWithPrintsAsync(_factory, TestUserId, $"set-{Guid.NewGuid():N}");

        var dto = await BuildPutDtoAsync(projectId);
        dto.StartDateOverride = new DateOnly(2026, 1, 1);
        var body = await PutProjectAsync(projectId, dto);

        Assert.Equal(new DateOnly(2026, 1, 1), body.StartDate);
        Assert.Equal(new DateOnly(2026, 1, 1), body.StartDateOverride);
        Assert.Equal(new DateOnly(2026, 3, 6), body.FinishDate);
        Assert.Null(body.FinishDateOverride);
    }

    [Fact]
    public async Task PutProject_SetsBothOverrides()
    {
        var projectId = await SeedProjectWithPrintsAsync(_factory, TestUserId, $"both-{Guid.NewGuid():N}");

        var dto = await BuildPutDtoAsync(projectId);
        dto.StartDateOverride = new DateOnly(2026, 1, 1);
        dto.FinishDateOverride = new DateOnly(2026, 12, 31);
        var body = await PutProjectAsync(projectId, dto);

        Assert.Equal(new DateOnly(2026, 1, 1), body.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), body.FinishDate);
    }

    [Fact]
    public async Task PutProject_NullOverrideClearsBackToAutomatic()
    {
        var projectId = await SeedProjectWithPrintsAsync(_factory, TestUserId, $"clear-{Guid.NewGuid():N}");

        var set = await BuildPutDtoAsync(projectId);
        set.StartDateOverride = new DateOnly(2026, 1, 1);
        await PutProjectAsync(projectId, set);

        var clear = await BuildPutDtoAsync(projectId);
        clear.StartDateOverride = null;
        var body = await PutProjectAsync(projectId, clear);

        Assert.Null(body.StartDateOverride);
        Assert.Equal(new DateOnly(2026, 3, 2), body.StartDate);
    }

    [Fact]
    public async Task PostProject_ReturnsResolvedDatesForAProjectWithNoPrints()
    {
        var dto = new AddProjectDto
        {
            Name = $"fresh-{Guid.NewGuid():N}",
            Status = Models.Project.ProjectStatus.InProgress,
            ViewStatus = Models.Project.ProjectViewStatus.Private,
        };

        var body = await PostProjectAsync(dto);

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), body.StartDate);
        Assert.Null(body.FinishDate);
    }

    [Fact]
    public async Task PostProject_AcceptsOverridesAtCreation()
    {
        var dto = new AddProjectDto
        {
            Name = $"fresh-pinned-{Guid.NewGuid():N}",
            Status = Models.Project.ProjectStatus.InProgress,
            ViewStatus = Models.Project.ProjectViewStatus.Private,
            StartDateOverride = new DateOnly(2026, 2, 1),
            FinishDateOverride = new DateOnly(2026, 2, 20),
        };

        var body = await PostProjectAsync(dto);

        Assert.Equal(new DateOnly(2026, 2, 1), body.StartDate);
        Assert.Equal(new DateOnly(2026, 2, 20), body.FinishDate);
        Assert.Equal(new DateOnly(2026, 2, 1), body.StartDateOverride);
    }

    [Fact]
    public async Task GetProjectSummaries_IncludeResolvedDates()
    {
        var name = $"summary-{Guid.NewGuid():N}";
        await SeedProjectWithPrintsAsync(_factory, TestUserId, name);

        // The test database is shared: filter by the unique name, never call Single() on the page.
        var page = await GetProjectSummariesAsync(search: name);
        var summary = Assert.Single(page.Items!, i => i.Name == name);

        Assert.Equal(new DateOnly(2026, 3, 2), summary.StartDate);
        Assert.Equal(new DateOnly(2026, 3, 6), summary.FinishDate);
    }

    [Fact]
    public async Task RecomputesAfterAPrintStartDateChanges()
    {
        var projectId = await SeedProjectWithPrintsAsync(_factory, TestUserId, $"recompute-{Guid.NewGuid():N}");
        Assert.Equal(new DateOnly(2026, 3, 2), (await GetProjectDetailAsync(projectId)).StartDate);

        await MoveEarliestPrintStartAsync(projectId, DateTimeOffset.Parse("2026-02-01T10:00:00Z"));

        // Dates are computed on read, so one representative path proves recomputation for all of them.
        Assert.Equal(new DateOnly(2026, 2, 1), (await GetProjectDetailAsync(projectId)).StartDate);
    }
}
