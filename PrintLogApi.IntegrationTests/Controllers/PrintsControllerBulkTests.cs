using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrintsControllerBulkTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;

    public PrintsControllerBulkTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private async Task<PrintDetailDTO> CreatePrintAsync(string title)
    {
        var newPrint = new AddPrintDTO
        {
            Title = title,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Private,
            AllowComments = false
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<ProjectDetailDto> CreateProjectAsync(string name)
    {
        var dto = new AddProjectDto
        {
            Name = name,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Projects");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(dto);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectDetailDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> PostBulkAsync(string path, object body, string? oauthId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId ?? IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BulkUpdate_AppliesEveryFieldAndLeavesOmittedFieldsAlone()
    {
        // Arrange
        var printOne = await CreatePrintAsync("Bulk Update One");
        var printTwo = await CreatePrintAsync("Bulk Update Two");
        var project = await CreateProjectAsync("Bulk Update Project");

        // Every field the contract supports except allowComments, which is deliberately
        // omitted so the test can prove an omitted field is left alone.
        var body = new
        {
            printIds = new[] { printOne.Id, printTwo.Id },
            status = (int)PrintStatus.Success,
            projectId = project.Id,
            viewStatus = (int)PrintViewStatus.Public,
            printerId = IntegrationTestSeeder.TestPrinterId2,
            allowFileDownloads = true
        };

        // Act
        var response = await PostBulkAsync("/api/Prints/bulk-update", body);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(new[] { printOne.Id, printTwo.Id }.OrderBy(id => id), result.Succeeded.OrderBy(id => id));
        Assert.Empty(result.Failed);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await db.Prints.AsNoTracking()
            .Where(p => p.Id == printOne.Id || p.Id == printTwo.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(stored, p =>
        {
            Assert.Equal(PrintStatus.Success, p.Status);
            Assert.Equal(project.Id, p.ProjectId);
            Assert.Equal(PrintViewStatus.Public, p.ViewStatus);
            Assert.Equal(IntegrationTestSeeder.TestPrinterId2, p.PrinterId);
            Assert.True(p.AllowFileDownloads);
            // Not in the request, so it must not have moved off its created value.
            Assert.False(p.AllowComments);
        });
        Assert.Contains(stored, p => p.Title == "Bulk Update One");
    }

    [Fact]
    public async Task BulkUpdate_ClearProjectId_RemovesTheAssignment()
    {
        var print = await CreatePrintAsync("Bulk Clear Project");
        var project = await CreateProjectAsync("Bulk Clear Project Target");

        await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            projectId = project.Id
        });

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            clear = new[] { "projectId" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await db.Prints.AsNoTracking()
            .FirstAsync(p => p.Id == print.Id, TestContext.Current.CancellationToken);
        Assert.Null(stored.ProjectId);
    }
}
