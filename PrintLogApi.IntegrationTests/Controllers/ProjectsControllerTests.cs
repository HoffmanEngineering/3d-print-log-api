using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using Xunit;
using System.Net.Http.Headers;

namespace PrintLogApi.IntegrationTests.Controllers
{
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
            return await resp.Content.ReadFromJsonAsync<ProjectDetailDto>();
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
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>())!;
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

            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<ProjectDetailDto>())!;
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
            var createResp = await _client.SendAsync(createReq);
            var created = (await createResp.Content.ReadFromJsonAsync<ProjectDetailDto>())!;

            // Get
            var getReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{created.Id}");
            var getResp = await _client.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            var result = (await getResp.Content.ReadFromJsonAsync<ProjectDetailDto>())!;
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
            var imgResp = await _client.SendAsync(imgReq);
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

            var response = await _client.SendAsync(imgReq);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var image = (await response.Content.ReadFromJsonAsync<ProjectImageDto>())!;
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
            var response = await _client.SendAsync(req);

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
            var response = await _client.SendAsync(req);

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
            var deleteResp = await _client.SendAsync(deleteReq);
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
            var deleteResp = await _client.SendAsync(deleteReq);
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
            await _client.SendAsync(createReq);

            var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search={uniqueName}&PageNumber=1&PageSize=10");
            var response = await _client.SendAsync(searchReq);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>())!;
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
            await _client.SendAsync(createReq);

            var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search={uniqueRef}&PageNumber=1&PageSize=10");
            var response = await _client.SendAsync(searchReq);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>())!;
            Assert.Contains(result.Items, p => p.Reference == uniqueRef);
        }

        [Fact]
        public async Task GetProjects_Search_WithNoMatch_ReturnsEmptyList()
        {
            var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search=NOMATCH-{Guid.NewGuid():N}&PageNumber=1&PageSize=10");
            var response = await _client.SendAsync(searchReq);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>())!;
            Assert.Empty(result.Items);
        }

        [Fact]
        public async Task GetProjects_Search_DoesNotReturnNonMatchingProjects()
        {
            var matchTerm = $"MATCH-{Guid.NewGuid():N}";
            var otherName = $"OTHER-{Guid.NewGuid():N}";

            var matchReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
            matchReq.Content = JsonContent.Create(new AddProjectDto { Name = matchTerm, Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private });
            await _client.SendAsync(matchReq);

            var otherReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
            otherReq.Content = JsonContent.Create(new AddProjectDto { Name = otherName, Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private });
            var otherResp = await _client.SendAsync(otherReq);
            var otherProject = (await otherResp.Content.ReadFromJsonAsync<ProjectDetailDto>())!;

            var searchReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects?search={matchTerm}&PageNumber=1&PageSize=100");
            var response = await _client.SendAsync(searchReq);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>())!;
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
            var createResp = await _client.SendAsync(createReq);
            var project = (await createResp.Content.ReadFromJsonAsync<ProjectDetailDto>())!;

            // Delete
            var deleteReq = AuthenticatedRequest(HttpMethod.Delete, $"/api/Projects/{project.Id}?deletePrints=false");
            var deleteResp = await _client.SendAsync(deleteReq);
            Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

            // Confirm gone
            var getReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{project.Id}");
            var getResp = await _client.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
        }
    }
}
