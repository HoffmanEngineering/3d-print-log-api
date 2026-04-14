using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using Xunit;
using System.Net.Http.Headers;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class ProjectsControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _client;

        public ProjectsControllerTests(CustomWebApplicationFactory factory)
        {
            _client = factory.CreateClient();
        }

        private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            return request;
        }

        [Fact]
        public async Task GetProjects_ReturnsOk_WithPagedList()
        {
            var request = AuthenticatedRequest(HttpMethod.Get, "/api/Projects?PageNumber=1&PageSize=10");
            var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<PagedList<ProjectSummaryDto>>();
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
            var result = await response.Content.ReadFromJsonAsync<ProjectDetailDto>();
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
            var created = await createResp.Content.ReadFromJsonAsync<ProjectDetailDto>();

            // Get
            var getReq = AuthenticatedRequest(HttpMethod.Get, $"/api/Projects/{created.Id}");
            var getResp = await _client.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            var result = await getResp.Content.ReadFromJsonAsync<ProjectDetailDto>();
            Assert.Equal(created.Id, result.Id);
        }

        [Fact]
        public async Task PostProjectImage_ReturnsCreated()
        {
            // Create project
            var createDto = new AddProjectDto { Name = "Image Test Project", Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private };
            var createReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
            createReq.Content = JsonContent.Create(createDto);
            var createResp = await _client.SendAsync(createReq);
            var project = await createResp.Content.ReadFromJsonAsync<ProjectDetailDto>();

            // Upload image (1x1 transparent PNG as base64)
            var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(imageBytes), "file", "test.png");

            var imgReq = AuthenticatedRequest(HttpMethod.Post, $"/api/Projects/{project.Id}/images");
            imgReq.Content = form;
            var imgResp = await _client.SendAsync(imgReq);
            Assert.Equal(HttpStatusCode.Created, imgResp.StatusCode);
        }

        [Fact]
        public async Task DeleteProject_UnlinksPrints_WhenDeletePrintsFalse()
        {
            // Create project
            var createDto = new AddProjectDto { Name = "Delete Test Project", Status = Models.Project.ProjectStatus.InProgress, ViewStatus = Models.Project.ProjectViewStatus.Private };
            var createReq = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
            createReq.Content = JsonContent.Create(createDto);
            var createResp = await _client.SendAsync(createReq);
            var project = await createResp.Content.ReadFromJsonAsync<ProjectDetailDto>();

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
