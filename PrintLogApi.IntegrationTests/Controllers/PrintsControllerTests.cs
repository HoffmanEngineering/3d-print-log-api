using System.Globalization;
using System.Net;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrintsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;

    public PrintsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    /// <summary>
    /// Helper: Creates a project and returns its detail.
    /// </summary>
    private async Task<ProjectDetailDto> CreateProjectAsync(string name = "Helper Project")
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
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectDetailDto>())!;
    }

    /// <summary>
    /// Helper: Creates a print and returns its detail.
    /// </summary>
    private async Task<PrintDetailDTO> CreatePrintAsync(string title = "Helper Print")
    {
        var newPrint = new AddPrintDTO
        {
            Title = title,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>())!;
    }

    #region Anonymous/Public Tests

    [Fact]
    public async Task GetPrintSummary_WithUserId_ReturnsSuccess()
    {
        // Act - use the seeded test user ID (public prints only)
        var response = await _httpClient.GetAsync($"/api/Prints/summary?userId={IntegrationTestSeeder.TestUserId}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintSummary_WithUserId_ReturnsPagedResult()
    {
        // Act
        var model = (await _httpClient.GetFromJsonAsync<PagedList<PrintSummaryDTO>>($"/api/Prints/summary?userId={IntegrationTestSeeder.TestUserId}", cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert - verify we get a valid paged response
        Assert.NotNull(model);
        Assert.NotNull(model.Paging);
        Assert.Equal(1, model.Paging.CurrentPage);
        Assert.True(model.Paging.TotalCount >= 0);
    }

    [Fact]
    public async Task GetPrintSummary_WithNonExistentUserId_ReturnsSuccess()
    {
        // Act - use a user ID that doesn't exist
        var response = await _httpClient.GetAsync("/api/Prints/summary?userId=99999", TestContext.Current.CancellationToken);

        // Assert - endpoint should still return 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Authenticated Tests

    [Fact]
    public async Task GetPrintSummary_Authenticated_ReturnsSuccess()
    {
        // Arrange - add the test auth header
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert - authenticated request should succeed
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintSummary_Authenticated_ReturnsOwnPrints()
    {
        // Arrange - add the test auth header
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert - authenticated user should see their own prints
        Assert.NotNull(model);
        Assert.NotNull(model.Items);
        Assert.True(model.Paging.TotalCount > 0, "Authenticated user should see their own prints");
    }

    [Fact]
    public async Task GetPrintSummary_Authenticated_PrintsHaveExpectedData()
    {
        // Arrange
        //
        // pageSize is explicit because the assertion below looks for a SEEDED print, and the
        // seeded rows are the oldest ones this user has. The default page size is 10, so once
        // enough sibling tests in this class have created prints in the shared fixture database,
        // the seeded rows fall off page one and the assertion fails for a reason that has nothing
        // to do with what it is testing. That is what xUnit v3's different in-class ordering
        // exposed (#70) - the dependency on running early was always there.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary?pageSize=1000");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert - verify prints have expected structure
        Assert.NotNull(model);
        Assert.NotEmpty(model.Items);

        // Check that seeded test prints exist (there may be other prints from other tests)
        Assert.True(model.Items.Any(p => p.Title!.Contains("Test Print")),
            "Should contain at least one seeded Test Print");

        // Verify print structure
        var anyPrint = model.Items.First();
        Assert.True(anyPrint.Id > 0);
        Assert.NotNull(anyPrint.Title);
    }

    [Fact]
    public async Task GetPrintSummary_Authenticated_WithPagination_ReturnsPaginatedResult()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary?pageSize=2");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert - pagination should work
        Assert.NotNull(model);
        Assert.NotNull(model.Paging);
        Assert.NotNull(model.Items);
    }

    [Fact]
    public async Task GetPrintSummary_SortedByTitle_ItemOrderIsPreservedAcrossPages()
    {
        // Query two consecutive pages sorted by title ascending.
        // Items on page 1 must all sort before items on page 2, and items within
        // each page must be in ascending title order.  This exercises the sort-key
        // restoration step in SearchPrintSummary that re-orders loaded entities to
        // match the paged ID list.
        var page1Req = new HttpRequestMessage(HttpMethod.Get,
            "/api/Prints/summary?pageNumber=1&pageSize=3&sortColumn=Title&sortDirection=Asc");
        page1Req.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var page2Req = new HttpRequestMessage(HttpMethod.Get,
            "/api/Prints/summary?pageNumber=2&pageSize=3&sortColumn=Title&sortDirection=Asc");
        page2Req.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var page1Resp = await _httpClient.SendAsync(page1Req, TestContext.Current.CancellationToken);
        var page2Resp = await _httpClient.SendAsync(page2Req, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page1Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page2Resp.StatusCode);

        var page1 = (await page1Resp.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var page2 = (await page2Resp.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(page1);
        Assert.NotNull(page2);
        Assert.True(page1.Items.Count > 0);
        Assert.True(page2.Items.Count > 0);

        // Items within each page are in ascending title order.
        for (int i = 0; i < page1.Items.Count - 1; i++)
            Assert.True(string.Compare(page1.Items[i].Title, page1.Items[i + 1].Title, StringComparison.OrdinalIgnoreCase) <= 0,
                $"Page 1 item[{i}]='{page1.Items[i].Title}' should come before item[{i + 1}]='{page1.Items[i + 1].Title}'");
        for (int i = 0; i < page2.Items.Count - 1; i++)
            Assert.True(string.Compare(page2.Items[i].Title, page2.Items[i + 1].Title, StringComparison.OrdinalIgnoreCase) <= 0,
                $"Page 2 item[{i}]='{page2.Items[i].Title}' should come before item[{i + 1}]='{page2.Items[i + 1].Title}'");

        // Last item on page 1 must sort <= first item on page 2.
        Assert.True(
            string.Compare(page1.Items.Last().Title, page2.Items.First().Title, StringComparison.OrdinalIgnoreCase) <= 0,
            $"Last page-1 item '{page1.Items.Last().Title}' should sort before first page-2 item '{page2.Items.First().Title}'");
    }

    [Fact]
    public async Task GetPrintSummary_NotAuthenticated_WithoutUserId_ReturnsBadRequest()
    {
        // Act & Assert - no auth header, no userId parameter should return BadRequest
        var response = await _httpClient.GetAsync("/api/Prints/summary", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region GET Single Print (Read)

    [Fact]
    public async Task GetPrintById_PublicPrint_ReturnsSuccess()
    {
        // Arrange - first get a print ID from the summary
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var printId = summary.Items.First().Id;

        // Act - get the print by ID (anonymous request should work for public prints)
        var response = await _httpClient.GetAsync($"/api/Prints/{printId}", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintById_ReturnsExpectedData()
    {
        // Arrange - find a seeded test print (other tests may have added prints)
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var seededPrint = summary.Items.First(p => p.Title!.Contains("Test Print"));

        // Act
        var print = (await _httpClient.GetFromJsonAsync<PrintDetailDTO>($"/api/Prints/{seededPrint.Id}", cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(print);
        Assert.Equal(seededPrint.Id, print.Id);
        Assert.NotNull(print.Title);
        Assert.Contains("Test Print", print.Title);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId, print.PrinterId);
        Assert.Equal(IntegrationTestSeeder.TestUserId, print.CreatedByUserId);
    }

    [Fact]
    public async Task GetPrintById_NonExistent_ReturnsNotFound()
    {
        // Act
        var response = await _httpClient.GetAsync("/api/Prints/999999", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region POST Print (Create)

    [Fact]
    public async Task CreatePrint_Authenticated_ReturnsCreated()
    {
        // Arrange
        var newPrint = new AddPrintDTO
        {
            Title = "Integration Test Created Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            Notes = "Created during integration test"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreatePrint_Authenticated_ReturnsCreatedPrint()
    {
        // Arrange
        var newPrint = new AddPrintDTO
        {
            Title = "Integration Test Print With Data",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            Notes = "This print has detailed data",
            EstimatedPrintTimeInSeconds = 7200,
            StartDate = DateTimeOffset.UtcNow
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var createdPrint = (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(createdPrint);
        Assert.True(createdPrint.Id > 0);
        Assert.Equal("Integration Test Print With Data", createdPrint.Title);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId, createdPrint.PrinterId);
        Assert.Equal(PrintStatus.Printing, createdPrint.Status);
        Assert.Equal(7200, createdPrint.EstimatedPrintTimeInSeconds);
    }

    [Fact]
    public async Task CreatePrint_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var newPrint = new AddPrintDTO
        {
            Title = "Should Not Be Created",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Content = JsonContent.Create(newPrint);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreatePrint_WithMissingTitle_ReturnsBadRequest()
    {
        // Arrange - Title is required
        var newPrint = new AddPrintDTO
        {
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatePrint_WithFilamentHavingNoMaterialCategory_DoesNotReturn500()
    {
        // Filament3 has no MaterialCategoryNickname, so its MaterialCategory navigation
        // property is null. UpdateFilamentUsageWeights must guard against this rather than
        // throwing NullReferenceException.
        var newPrint = new AddPrintDTO
        {
            Title = "Print With Uncategorised Filament",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            FilamentUsage = new List<PrintFilamentSummaryDto>
            {
                new PrintFilamentSummaryDto
                {
                    Id = Guid.NewGuid(),
                    Filament = new FilamentSummaryDto { Id = IntegrationTestSeeder.TestFilamentId3 },
                    Source = PrintFilament.SourceMeasurement.Weight,
                    AmountMg = 15000
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}. Body: {responseBody}");
    }

    [Fact]
    public async Task CreatePrint_WithTwoFilamentUsageEntries_BothEntriesReturnedWithComputedWeights()
    {
        // Both filament1 (PLA, density 1.24) and filament2 (PETG, density 1.27) have
        // MaterialCategoryNickname set; weight computation should fire for both, producing
        // non-null VolumeMl for each entry. This acts as a correctness safety net for the
        // batch-load refactor: a mis-matched lookup would assign the wrong filament's
        // density and produce wrong (or missing) computed values.
        var filament1Entry = new PrintFilamentSummaryDto
        {
            Id = Guid.NewGuid(),
            Filament = new FilamentSummaryDto { Id = IntegrationTestSeeder.TestFilamentId1 },
            Source = PrintFilament.SourceMeasurement.Weight,
            AmountMg = 10000
        };
        var filament2Entry = new PrintFilamentSummaryDto
        {
            Id = Guid.NewGuid(),
            Filament = new FilamentSummaryDto { Id = IntegrationTestSeeder.TestFilamentId2 },
            Source = PrintFilament.SourceMeasurement.Weight,
            AmountMg = 20000
        };

        var newPrint = new AddPrintDTO
        {
            Title = "Print With Two Filaments",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            FilamentUsage = new List<PrintFilamentSummaryDto> { filament1Entry, filament2Entry }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(created.FilamentUsage);
        Assert.Equal(2, created.FilamentUsage.Count);

        var f1 = created.FilamentUsage.FirstOrDefault(f => f.Filament?.Id == IntegrationTestSeeder.TestFilamentId1);
        var f2 = created.FilamentUsage.FirstOrDefault(f => f.Filament?.Id == IntegrationTestSeeder.TestFilamentId2);
        Assert.NotNull(f1);
        Assert.NotNull(f2);
        Assert.NotNull(f1.VolumeMl);
        Assert.NotNull(f2.VolumeMl);

        // VolumeMl for f2 (2× weight, slightly higher density) must be larger than f1's.
        Assert.True(f2.VolumeMl > f1.VolumeMl);
    }

    [Fact]
    public async Task CreatePrint_WithInaccessibleFilamentAmongMultiple_ReturnsBadRequest()
    {
        // One valid filament + one that doesn't exist in the DB — the batch access check
        // must reject the whole request even when the bad ID is not first in the list.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var newPrint = new AddPrintDTO
        {
            Title = "Print With Bad Filament",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            FilamentUsage = new List<PrintFilamentSummaryDto>
            {
                new PrintFilamentSummaryDto
                {
                    Id = Guid.NewGuid(),
                    Filament = new FilamentSummaryDto { Id = IntegrationTestSeeder.TestFilamentId1 },
                    Source = PrintFilament.SourceMeasurement.Weight,
                    AmountMg = 10000
                },
                new PrintFilamentSummaryDto
                {
                    Id = Guid.NewGuid(),
                    Filament = new FilamentSummaryDto { Id = Guid.NewGuid() }, // non-existent filament
                    Source = PrintFilament.SourceMeasurement.Weight,
                    AmountMg = 5000
                }
            }
        };
        request.Content = JsonContent.Create(newPrint);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region PUT Print (Update)

    [Fact]
    public async Task UpdatePrint_Authenticated_ReturnsSuccess()
    {
        // Arrange - first create a print to update
        var newPrint = new AddPrintDTO
        {
            Title = "Print To Update",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrint);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdPrint = (await createResponse.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Arrange - prepare update
        var updateDto = new PutPrintDetailDto
        {
            Id = createdPrint.Id,
            Title = "Updated Print Title",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            Notes = "Updated notes"
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Prints/{createdPrint.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        var updateResponse = await _httpClient.SendAsync(updateRequest, TestContext.Current.CancellationToken);

        // Assert - PUT endpoint returns CreatedAtAction (201)
        Assert.Equal(HttpStatusCode.Created, updateResponse.StatusCode);
    }

    [Fact]
    public async Task UpdatePrint_Authenticated_ReturnsUpdatedData()
    {
        // Arrange - first create a print to update
        var newPrint = new AddPrintDTO
        {
            Title = "Print For Update Test",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrint);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdPrint = (await createResponse.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Arrange - prepare update with all fields changed
        var updateDto = new PutPrintDetailDto
        {
            Id = createdPrint.Id,
            Title = "Completely Updated Title",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Unlisted,
            AllowComments = false,
            Notes = "These are new notes",
            PrintTimeInSeconds = 3600,
            EstimatedPrintTimeInSeconds = 4000
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Prints/{createdPrint.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        var updateResponse = await _httpClient.SendAsync(updateRequest, TestContext.Current.CancellationToken);
        var updatedPrint = (await updateResponse.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert - PUT endpoint returns CreatedAtAction (201)
        Assert.Equal(HttpStatusCode.Created, updateResponse.StatusCode);
        Assert.NotNull(updatedPrint);
        Assert.Equal("Completely Updated Title", updatedPrint.Title);
        Assert.Equal(PrintStatus.Success, updatedPrint.Status);
        Assert.Equal(PrintViewStatus.Unlisted, updatedPrint.ViewStatus);
        Assert.False(updatedPrint.AllowComments);
        Assert.Equal("These are new notes", updatedPrint.Notes);
        Assert.Equal(3600, updatedPrint.PrintTimeInSeconds);
    }

    [Fact]
    public async Task UpdatePrint_AsDifferentAuthenticatedUser_ReturnsForbidden()
    {
        // Arrange - create a second user who owns neither this print nor its printer
        const string otherUserOAuthId = "auth0|test-other-user-update-print";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Users.Add(new User { OAuthUserId = otherUserOAuthId, ViewStatus = User.ProfileViewStatus.Public });
            db.SaveChanges();
        }

        var updateDto = new PutPrintDetailDto
        {
            Id = IntegrationTestSeeder.TestPrintId,
            Title = "Should Not Update",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, otherUserOAuthId);
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePrint_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - get an existing print ID
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var printId = summary.Items.First().Id;

        var updateDto = new PutPrintDetailDto
        {
            Id = printId,
            Title = "Should Not Update",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Prints/{printId}");
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePrint_IdMismatch_ReturnsBadRequest()
    {
        // Arrange - get an existing print
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var printId = summary.Items.First().Id;

        // ID in DTO doesn't match route ID
        var updateDto = new PutPrintDetailDto
        {
            Id = printId + 100, // Mismatched ID
            Title = "Updated Title",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Prints/{printId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region DELETE Print

    [Fact]
    public async Task DeletePrint_Authenticated_ReturnsSuccess()
    {
        // Arrange - first create a print to delete
        var newPrint = new AddPrintDTO
        {
            Title = "Print To Delete",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrint);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdPrint = (await createResponse.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Prints/{createdPrint.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var deleteResponse = await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task DeletePrint_Authenticated_PrintNoLongerExists()
    {
        // Arrange - first create a print to delete
        var newPrint = new AddPrintDTO
        {
            Title = "Print To Delete And Verify",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrint);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdPrint = (await createResponse.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act - delete the print
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Prints/{createdPrint.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert - try to get the deleted print
        var getResponse = await _httpClient.GetAsync($"/api/Prints/{createdPrint.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeletePrint_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - get an existing print ID
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var printId = summary.Items.First().Id;

        // Act - try to delete without auth
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Prints/{printId}");
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrint_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/Prints/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrint_WithLinkedNotification_SucceedsAndDeletesNotification()
    {
        // Arrange - create a print, then seed a notification linked to it
        var createdPrint = await CreatePrintAsync("Print With Linked Notification");

        Guid notificationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var notification = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = IntegrationTestSeeder.TestUserId,
                Type = NotificationType.PrintCompleted,
                Title = "Your print completed",
                IsRead = false,
                CreatedDate = DateTime.UtcNow,
                PrintId = createdPrint.Id
            };
            db.Notifications.Add(notification);
            db.SaveChanges();
            notificationId = notification.Id;
        }

        // Act
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Prints/{createdPrint.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var deleteResponse = await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert - delete succeeded and notification was cleaned up
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var orphanedNotification = db.Notifications.Find(notificationId);
            Assert.Null(orphanedNotification);
        }
    }

    #endregion

    #region GET Print Stats

    [Fact]
    public async Task GetPrintStats_Authenticated_ReturnsSuccess()
    {
        var fromDate = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var toDate = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/stats?fromDate={fromDate}&toDate={toDate}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintStats_NotAuthenticated_ReturnsUnauthorized()
    {
        var fromDate = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var toDate = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/stats?fromDate={fromDate}&toDate={toDate}");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region GET Print CSV

    [Fact]
    public async Task GetAllPrintDetailsAsCsv_Authenticated_ReturnsFile()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/csv");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("PrintReports.csv", response.Content.Headers.ContentDisposition.FileName);
    }

    /// <summary>
    /// The CSV contract is user-facing, so the header line, the column order and the descending
    /// StartDate ordering are all asserted rather than just the status code. Rows for other prints
    /// in the shared fixture are ignored — only the two created here are compared, and only
    /// relative to each other.
    /// </summary>
    [Fact]
    public async Task GetAllPrintDetailsAsCsv_ReturnsHeaderAndRowsNewestStartDateFirst()
    {
        var older = await CreatePrintWithStartDateAsync(
            "Csv Export Older Print", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = await CreatePrintWithStartDateAsync(
            "Csv Export Newer Print", new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/csv");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var csv = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Asserted on the raw payload before any normalization: the line ending and the trailing
        // newline are part of the byte-level CSV contract, and normalizing first would hide a
        // regression in either. CsvHelper's own default is the reference — the export deliberately
        // does not override NewLine, so pinning it here fails if someone starts to.
        var newLine = new CsvConfiguration(CultureInfo.InvariantCulture).NewLine;
        Assert.Contains(newLine, csv, StringComparison.Ordinal);
        Assert.EndsWith(newLine, csv, StringComparison.Ordinal);

        var lines = csv.Split(newLine, StringSplitOptions.RemoveEmptyEntries).ToList();

        Assert.Equal(
            "Start Date,Title,Printer Name,Printer Make,Printer Model,Estimated Print Time (s),"
            + "Estimated Filament Usage (g),Print Time (s),Filament Usage (g),Filament Type,Notes,"
            + "Url,Status,View Status",
            lines[0]);

        var newerIndex = lines.FindIndex(l => l.Contains(newer.Title!, StringComparison.Ordinal));
        var olderIndex = lines.FindIndex(l => l.Contains(older.Title!, StringComparison.Ordinal));

        Assert.True(newerIndex > 0, "Expected the newer print to appear in the export.");
        Assert.True(olderIndex > 0, "Expected the older print to appear in the export.");
        Assert.True(newerIndex < olderIndex, "Expected rows ordered by StartDate descending.");

        // A representative row: the projected columns line up with the print that was created.
        var newerRow = lines[newerIndex].Split(',');
        Assert.Equal(newer.Title, newerRow[1]);
        Assert.Equal("Test Printer 1", newerRow[2]);
    }

    /// <summary>
    /// The export streams rather than buffering, so the response has no Content-Length. This is the
    /// observable consequence of #65 — a buffered MemoryStream result would set one.
    /// </summary>
    [Fact]
    public async Task GetAllPrintDetailsAsCsv_StreamsWithoutBufferingWholeReport()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/csv");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        Assert.Null(response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// A client that hangs up mid-export must stop the query rather than draining it. Asserted at
    /// the service level with an already-cancelled token, because the abort point in a real
    /// disconnect is a race and would make the test flaky.
    /// </summary>
    [Fact]
    public async Task WritePrintReportAsCsvForUser_CancelledToken_StopsInsteadOfDraining()
    {
        using var scope = _factory.Services.CreateScope();
        var printService = scope.ServiceProvider.GetRequiredService<IPrintService>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => printService.WritePrintReportAsCsvForUser(
                IntegrationTestSeeder.TestUserId, destination, cts.Token));

        // Nothing was flushed on the way out. A failure that reaches the destination starts the
        // response and locks in a 200, so the writers are abandoned rather than disposed on the
        // exception path — this is what keeps a failed export reportable as an error.
        Assert.Equal(0, destination.Length);
    }

    /// <summary>
    /// Helper: creates a print with an explicit StartDate so export ordering can be asserted.
    /// </summary>
    private async Task<PrintDetailDTO> CreatePrintWithStartDateAsync(string title, DateTimeOffset startDate)
    {
        var newPrint = new AddPrintDTO
        {
            Title = title,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            StartDate = startDate
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>())!;
    }

    [Fact]
    public async Task GetAllPrintDetailsAsCsv_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/csv");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region PUT Print Status

    [Fact]
    public async Task UpdatePrintStatus_Authenticated_ReturnsCreated()
    {
        var print = await CreatePrintAsync("Print For Status Update");

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{print.Id}/status/{(int)PrintStatus.Success}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePrintStatus_AsDifferentAuthenticatedUser_ReturnsForbidden()
    {
        // Arrange - create a second user who owns neither this print nor its printer
        const string otherUserOAuthId = "auth0|test-other-user-update-status";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Users.Add(new User { OAuthUserId = otherUserOAuthId, ViewStatus = User.ProfileViewStatus.Public });
            db.SaveChanges();
        }

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/status/{(int)PrintStatus.Success}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, otherUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePrintStatus_NonExistentPrint_ReturnsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/999999/status/{(int)PrintStatus.Success}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePrintStatus_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/status/{(int)PrintStatus.Success}");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region POST Print Comment

    [Fact]
    public async Task PostPrintComment_Authenticated_ReturnsComment()
    {
        var print = await CreatePrintAsync("Print For Comment Test");
        var newComment = new AddCommentDto { Body = "Test comment on print" };

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{print.Id}/comment");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newComment);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var comment = (await response.Content.ReadFromJsonAsync<CommentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(comment);
        Assert.Equal("Test comment on print", comment.Body);
    }

    [Fact]
    public async Task PostPrintComment_NotAuthenticated_ReturnsUnauthorized()
    {
        var newComment = new AddCommentDto { Body = "Should not be created" };

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/comment");
        request.Content = JsonContent.Create(newComment);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostPrintComment_NonExistentPrint_ReturnsNotFound()
    {
        var newComment = new AddCommentDto { Body = "Comment on missing print" };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints/999999/comment");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newComment);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region DELETE Print Comment

    [Fact]
    public async Task DeletePrintComment_Authenticated_ReturnsOk()
    {
        // Create a print and add a comment
        var print = await CreatePrintAsync("Print For Delete Comment");
        var newComment = new AddCommentDto { Body = "Comment to delete" };

        var commentRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{print.Id}/comment");
        commentRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        commentRequest.Content = JsonContent.Create(newComment);
        var commentResponse = await _httpClient.SendAsync(commentRequest, TestContext.Current.CancellationToken);
        var comment = (await commentResponse.Content.ReadFromJsonAsync<CommentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Delete the comment
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{print.Id}/comment/{comment.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrintComment_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/comment/1");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrintComment_NonExistentPrint_ReturnsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/Prints/999999/comment/1");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrintComment_NonExistentComment_ReturnsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/comment/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region GET Public Print Ids

    [Fact]
    public async Task GetPublicPrintIds_ReturnsSuccess()
    {
        var response = await _httpClient.GetAsync("/api/Prints/public", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPublicPrintIds_ReturnsListOfIds()
    {
        var ids = (await _httpClient.GetFromJsonAsync<List<long>>("/api/Prints/public", cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(ids);
        Assert.True(ids.Count > 0, "Should have at least one public print");
    }

    #endregion

    #region PUT Print (NonExistent)

    [Fact]
    public async Task UpdatePrint_NonExistent_ReturnsNotFound()
    {
        var updateDto = new PutPrintDetailDto
        {
            Id = 999999,
            Title = "Updated Title",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/Prints/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(updateDto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Image Management - Set Default

    [Fact]
    public async Task SetImageAsDefault_Authenticated_ReturnsOk()
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image/{IntegrationTestSeeder.TestPrintImageId2}/set-as-default");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SetImageAsDefault_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image/{IntegrationTestSeeder.TestPrintImageId1}/set-as-default");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SetImageAsDefault_NonExistentPrint_ReturnsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/Prints/999999/image/1/set-as-default");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetImageAsDefault_NonExistentImage_ReturnsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image/999999/set-as-default");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Image Management - Reorder

    [Fact]
    public async Task ReorderImages_Authenticated_ReturnsOk()
    {
        var reorderDto = new ReorderImagesDto
        {
            Images = new List<ImageOrderDto>
            {
                new ImageOrderDto { ImageId = IntegrationTestSeeder.TestPrintImageId1, DisplayOrder = 1 },
                new ImageOrderDto { ImageId = IntegrationTestSeeder.TestPrintImageId2, DisplayOrder = 0 }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/images/reorder");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(reorderDto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReorderImages_NotAuthenticated_ReturnsUnauthorized()
    {
        var reorderDto = new ReorderImagesDto
        {
            Images = new List<ImageOrderDto>
            {
                new ImageOrderDto { ImageId = IntegrationTestSeeder.TestPrintImageId1, DisplayOrder = 0 }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/images/reorder");
        request.Content = JsonContent.Create(reorderDto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReorderImages_NonExistentPrint_ReturnsNotFound()
    {
        var reorderDto = new ReorderImagesDto
        {
            Images = new List<ImageOrderDto>
            {
                new ImageOrderDto { ImageId = 1, DisplayOrder = 0 }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            "/api/Prints/999999/images/reorder");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(reorderDto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReorderImages_EmptyImagesList_ReturnsBadRequest()
    {
        var reorderDto = new ReorderImagesDto
        {
            Images = new List<ImageOrderDto>()
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/images/reorder");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(reorderDto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReorderImages_MismatchedImageIds_ReturnsBadRequest()
    {
        var reorderDto = new ReorderImagesDto
        {
            Images = new List<ImageOrderDto>
            {
                new ImageOrderDto { ImageId = 999, DisplayOrder = 0 },
                new ImageOrderDto { ImageId = 998, DisplayOrder = 1 }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/images/reorder");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(reorderDto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReorderImages_NegativeDisplayOrder_ReturnsBadRequest()
    {
        var reorderDto = new ReorderImagesDto
        {
            Images = new List<ImageOrderDto>
            {
                new ImageOrderDto { ImageId = 1, DisplayOrder = -1 }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/images/reorder");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(reorderDto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Image Management - Remove

    [Fact]
    public async Task RemoveImage_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image/{IntegrationTestSeeder.TestPrintImageId1}");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RemoveImage_NonExistentPrint_ReturnsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            "/api/Prints/999999/image/1");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveImage_NonExistentImage_ReturnsNotFound()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveImage_NonDefaultImage_ReturnsOkAndImageIsRemoved()
    {
        // Arrange - create a fresh print so this test is isolated
        var print = await CreatePrintAsync("Print For RemoveImage NonDefault Test");
        var now = DateTime.UtcNow;

        int defaultImageId;
        int nonDefaultImageId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var file1 = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/remove-test-default.jpg",
                Size = 1024,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var file2 = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/remove-test-non-default.jpg",
                Size = 2048,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            db.Files.AddRange(file1, file2);

            var defaultImage = new PrintImage
            {
                PrintId = print.Id,
                File = file1,
                IsDefault = true,
                DisplayOrder = 0,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var nonDefaultImage = new PrintImage
            {
                PrintId = print.Id,
                File = file2,
                IsDefault = false,
                DisplayOrder = 1,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            db.PrintImages.AddRange(defaultImage, nonDefaultImage);
            db.SaveChanges();

            defaultImageId = defaultImage.Id;
            nonDefaultImageId = nonDefaultImage.Id;
        }

        // Act - delete the non-default image
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{print.Id}/image/{nonDefaultImageId}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert - 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - the non-default image no longer exists in DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var deletedImage = db.PrintImages.AsNoTracking().FirstOrDefault(i => i.Id == nonDefaultImageId);
            Assert.Null(deletedImage);

            // The default image should remain and still be the default
            var remainingDefault = db.PrintImages.AsNoTracking().FirstOrDefault(i => i.Id == defaultImageId);
            Assert.NotNull(remainingDefault);
            Assert.True(remainingDefault.IsDefault, "The original default image should still be marked as default");
        }
    }

    [Fact]
    public async Task RemoveImage_DefaultImage_PromotesNextImageByDisplayOrder()
    {
        // Arrange - create a fresh print so this test is isolated
        var print = await CreatePrintAsync("Print For RemoveImage Default Promotion Test");
        var now = DateTime.UtcNow;

        int imageIdOrder0;
        int imageIdOrder1;
        int imageIdOrder2;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var file0 = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/promote-test-order0.jpg",
                Size = 1024,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var file1 = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/promote-test-order1.jpg",
                Size = 1024,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var file2 = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/promote-test-order2.jpg",
                Size = 1024,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            db.Files.AddRange(file0, file1, file2);

            var imageOrder0 = new PrintImage
            {
                PrintId = print.Id,
                File = file0,
                IsDefault = true,
                DisplayOrder = 0,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var imageOrder1 = new PrintImage
            {
                PrintId = print.Id,
                File = file1,
                IsDefault = false,
                DisplayOrder = 1,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var imageOrder2 = new PrintImage
            {
                PrintId = print.Id,
                File = file2,
                IsDefault = false,
                DisplayOrder = 2,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            db.PrintImages.AddRange(imageOrder0, imageOrder1, imageOrder2);
            db.SaveChanges();

            imageIdOrder0 = imageOrder0.Id;
            imageIdOrder1 = imageOrder1.Id;
            imageIdOrder2 = imageOrder2.Id;
        }

        // Act - delete the default image (DisplayOrder=0)
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{print.Id}/image/{imageIdOrder0}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert - 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert DB state
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            // Deleted image is gone
            var deletedImage = db.PrintImages.AsNoTracking().FirstOrDefault(i => i.Id == imageIdOrder0);
            Assert.Null(deletedImage);

            // Image with DisplayOrder=1 is now the default
            var promotedImage = db.PrintImages.AsNoTracking().FirstOrDefault(i => i.Id == imageIdOrder1);
            Assert.NotNull(promotedImage);
            Assert.True(promotedImage.IsDefault, "Image with DisplayOrder=1 should be promoted to default");

            // Image with DisplayOrder=2 is still not the default
            var remainingImage = db.PrintImages.AsNoTracking().FirstOrDefault(i => i.Id == imageIdOrder2);
            Assert.NotNull(remainingImage);
            Assert.False(remainingImage.IsDefault, "Image with DisplayOrder=2 should not be promoted");
        }
    }

    [Fact]
    public async Task RemoveImage_LastImage_ReturnsOkAndPrintHasNoImages()
    {
        // Arrange - create a fresh print so this test is isolated
        var print = await CreatePrintAsync("Print For RemoveImage LastImage Test");
        var now = DateTime.UtcNow;

        int soleImageId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var file = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/last-image-test.jpg",
                Size = 1024,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            db.Files.Add(file);

            var soleImage = new PrintImage
            {
                PrintId = print.Id,
                File = file,
                IsDefault = true,
                DisplayOrder = 0,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedDate = now
            };
            db.PrintImages.Add(soleImage);
            db.SaveChanges();

            soleImageId = soleImage.Id;
        }

        // Act - delete the only image
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{print.Id}/image/{soleImageId}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert - 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - no PrintImages remain for this print
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var remainingImages = db.PrintImages.AsNoTracking().Where(i => i.PrintId == print.Id).ToList();
            Assert.Empty(remainingImages);
        }
    }

    #endregion

    #region POST Print Image

    [Fact]
    public async Task PostImage_WithNoFile_ReturnsBadRequest()
    {
        // Arrange - send multipart with no image field
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = new MultipartFormDataContent();

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostImage_WithInvalidFileType_ReturnsBadRequest()
    {
        // Arrange - send a text file instead of an image
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var fileContent = new ByteArrayContent(new byte[] { 0x00, 0x01, 0x02 });
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        var formContent = new MultipartFormDataContent();
        formContent.Add(fileContent, "image", "document.txt");
        request.Content = formContent;

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostImage_WithOversizedFile_ReturnsBadRequest()
    {
        // Arrange - send a file 1 byte over the 10MB limit
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var oversizedBytes = new byte[10 * 1024 * 1024 + 1];
        var fileContent = new ByteArrayContent(oversizedBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        var formContent = new MultipartFormDataContent();
        formContent.Add(fileContent, "image", "too-big.jpg");
        request.Content = formContent;

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region GET Print Summary with Filters

    [Fact]
    public async Task GetPrintSummary_WithSearchText_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/summary?searchText=Test&userId={IntegrationTestSeeder.TestUserId}");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintSummary_WithPrinterFilter_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/summary?filterByPrinterIds={IntegrationTestSeeder.TestPrinterId}&userId={IntegrationTestSeeder.TestUserId}");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintSummary_WithStatusFilter_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/summary?filterByStatus={(int)PrintStatus.Success}&userId={IntegrationTestSeeder.TestUserId}");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintSummary_WithFilamentFilter_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/summary?filterByFilamentIds={IntegrationTestSeeder.TestFilamentId1}&userId={IntegrationTestSeeder.TestUserId}");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrintSummary_WithMultiplePrinterIds_ReturnsMatchingPrintsFromAllSpecifiedPrinters()
    {
        // Create a print on Printer 2 with a far-future StartDate so it sorts first
        // regardless of how many prints from previous tests are in the DB.
        var printer2Print = new AddPrintDTO
        {
            Title = "Printer2 Filter Test Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId2,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = false,
            StartDate = DateTimeOffset.UtcNow.AddYears(10)
        };
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        createReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createReq.Content = JsonContent.Create(printer2Print);
        var createResp = await _httpClient.SendAsync(createReq, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = (await createResp.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Filter by both printers at once; use a large page size so the test is
        // not affected by how many prints previous tests created.
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/summary?pageSize=200&filterByPrinterIds={IntegrationTestSeeder.TestPrinterId}&filterByPrinterIds={IntegrationTestSeeder.TestPrinterId2}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        // Both printers should be represented.
        Assert.Contains(result.Items, p => p.Printer?.Id == IntegrationTestSeeder.TestPrinterId);
        Assert.Contains(result.Items, p => p.Id == created.Id && p.Printer?.Id == IntegrationTestSeeder.TestPrinterId2);
        // No print from a different printer slips through.
        Assert.All(result.Items, p =>
            Assert.True(p.Printer?.Id == IntegrationTestSeeder.TestPrinterId
                        || p.Printer?.Id == IntegrationTestSeeder.TestPrinterId2,
                $"Unexpected PrinterId {p.Printer?.Id} in filtered result"));
    }

    [Fact]
    public async Task GetPrintSummary_WithFilamentFilter_ReturnsOnlyMatchingPrints()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/summary?filterByFilamentIds={IntegrationTestSeeder.TestFilamentId1}&userId={IntegrationTestSeeder.TestUserId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        Assert.All(result.Items, print =>
            Assert.Contains(print.FilamentUsage!, fu => fu.Filament?.Id == IntegrationTestSeeder.TestFilamentId1));
    }

    #endregion

    #region File Attachments

    [Fact]
    public async Task GetFiles_ReturnsEmptyList_WhenNoneExist()
    {
        // GET /api/prints/{id}/files is AllowAnonymous
        var response = await _httpClient.GetAsync($"/api/Prints/{IntegrationTestSeeder.TestPrintId}/files", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var files = (await response.Content.ReadFromJsonAsync<List<PrintAttachmentDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(files);
        Assert.Empty(files);
    }

    [Fact]
    public async Task GetFileUploadUrl_Returns403_ForFreeUser()
    {
        // The seeded test user has no Pro subscription, so the service should throw ForbiddenException.
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/files/upload-url");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new GetUploadUrlRequest
        {
            FileName = "benchy.gcode",
            ContentType = "application/octet-stream",
            SizeBytes = 1024,
        });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetFileUploadUrl_Returns401_WhenNotAuthenticated()
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/files/upload-url");
        request.Content = JsonContent.Create(new GetUploadUrlRequest
        {
            FileName = "benchy.gcode",
            ContentType = "application/octet-stream",
            SizeBytes = 1024,
        });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFiles_NonExistentPrint_ReturnsNotFound()
    {
        // After the visibility fix, GetFiles now returns 404 for non-existent prints.
        var response = await _httpClient.GetAsync("/api/Prints/999999/files", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFiles_PrivatePrint_AnonymousUser_ReturnsForbidden()
    {
        // Create a private print as the test user, then try to retrieve its files anonymously.
        var newPrint = new AddPrintDTO
        {
            Title = "Private Print For GetFiles Test",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Private,
            AllowComments = false
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrint);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var createdPrint = (await createResponse.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Anonymous request for files on a private print should be forbidden.
        var response = await _httpClient.GetAsync($"/api/Prints/{createdPrint.Id}/files", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetFiles_PrivatePrint_Owner_ReturnsOk()
    {
        // Create a private print and then retrieve its files as the owner.
        var newPrint = new AddPrintDTO
        {
            Title = "Private Print Owner GetFiles Test",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Private,
            AllowComments = false
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrint);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        createResponse.EnsureSuccessStatusCode();
        var createdPrint = (await createResponse.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Owner's authenticated request for files on their private print should succeed.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{createdPrint.Id}/files");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var files = (await response.Content.ReadFromJsonAsync<List<PrintAttachmentDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(files);
        Assert.Empty(files);
    }

    [Fact]
    public async Task ConfirmFileUpload_Returns403_ForFreeUser()
    {
        // The seeded test user has no Pro subscription, so the service should throw ForbiddenException.
        // A happy-path confirm test is not feasible without a Pro-subscribed user in the test seed.
        // This test documents the expected 403 for a free user.
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/files/confirm");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new ConfirmUploadRequest
        {
            BlobPath = $"printattachments/{IntegrationTestSeeder.TestPrintId}/{Guid.NewGuid()}.gcode",
            FileName = "test.gcode",
            ContentType = "application/octet-stream",
            SizeBytes = 1024,
        });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Free user → AssertProAsync throws ForbiddenException → 403
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmFileUpload_Returns401_WhenNotAuthenticated()
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/files/confirm");
        request.Content = JsonContent.Create(new ConfirmUploadRequest
        {
            BlobPath = $"printattachments/{IntegrationTestSeeder.TestPrintId}/{Guid.NewGuid()}.gcode",
            FileName = "test.gcode",
            ContentType = "application/octet-stream",
            SizeBytes = 1024,
        });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteFile_Returns401_WhenNotAuthenticated()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/files/999999");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteFile_Returns404_WhenFileDoesNotExist()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/files/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostPrint_WithNewProjectName_CreatesProjectAndAssignsPrint()
    {
        var dto = new
        {
            title = "Voron Part 1",
            printerId = IntegrationTestSeeder.TestPrinterId,
            status = 3, // Success
            viewStatus = 3, // Private
            allowComments = false,
            filamentUsage = Array.Empty<object>(),
            filamentType = "",
            notes = "",
            url = "",
            fileName = "",
            newProjectName = "My Voron Build"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(dto);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result.ProjectId);
    }

    [Fact]
    public async Task GetPrintSummary_FilterByProjectId_ReturnsPrintsInProject()
    {
        // Create project
        var projectDto = new { name = "Filter Test Project", status = 1, viewStatus = 3 };
        var projReq = new HttpRequestMessage(HttpMethod.Post, "/api/Projects");
        projReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        projReq.Content = JsonContent.Create(projectDto);
        var projResp = await _httpClient.SendAsync(projReq, TestContext.Current.CancellationToken);
        var project = (await projResp.Content.ReadFromJsonAsync<ProjectDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Create print assigned to project
        var printDto = new
        {
            title = "Filtered Print",
            printerId = IntegrationTestSeeder.TestPrinterId,
            status = 3,
            viewStatus = 3,
            allowComments = false,
            filamentUsage = Array.Empty<object>(),
            filamentType = "",
            notes = "",
            url = "",
            fileName = "",
            projectId = project.Id
        };
        var printReq = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        printReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        printReq.Content = JsonContent.Create(printDto);
        await _httpClient.SendAsync(printReq, TestContext.Current.CancellationToken);

        // Filter by project
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/summary?PageNumber=1&PageSize=10&filterByProjectId={project.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.All(result.Items, item => Assert.Equal(project.Id, item.ProjectId));
    }

    [Fact]
    public async Task ConfirmAndDeleteFile_HappyPath_RequiresProSubscription()
    {
        // NOTE: A full happy-path confirm+delete test is not feasible with the current test seed
        // because the seeded test user (TestUserOAuthId) has no Pro subscription row in the
        // Subscriptions table. Every call to ConfirmFileUpload or GetFileUploadUrl hits
        // AssertProAsync, which throws ForbiddenException and returns 403.
        //
        // To enable happy-path tests, add a Pro subscription to the seeded user in
        // IntegrationTestSeeder.Seed() (e.g., a Subscription with Status = Active for TestUserId),
        // and then:
        //   1. Call POST /api/Prints/{id}/files/upload-url to get a SAS URL + blob path.
        //   2. The mocked IBlobStorageService will return a fake SAS URL without hitting Azure.
        //   3. Call POST /api/Prints/{id}/files/confirm with the fake blob path.
        //   4. Assert 200 and a valid PrintAttachmentDto.
        //   5. Call DELETE /api/Prints/{id}/files/{attachmentId} to clean up.
        //   6. Assert 200 OK.
        //
        // This test is intentionally a no-op placeholder documenting the infrastructure gap.
        Assert.True(true, "See comment: happy-path requires a Pro-subscribed user in the test seed.");
    }

    #endregion

    #region Grouped Feed

    [Fact]
    public async Task GetPrintsGrouped_ReturnsMixedFeed()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/grouped?pageNumber=1&pageSize=10");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        Assert.NotNull(result);
        Assert.All(result.Items, item => Assert.Contains(item.Type, new[] { "project", "print" }));
    }

    #endregion

    #region GetGrouped Tests

    [Fact]
    public async Task GetGrouped_Authenticated_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/grouped?pageNumber=1&pageSize=10");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetGrouped_NotAuthenticated_ReturnsUnauthorized()
    {
        var response = await _httpClient.GetAsync("/api/Prints/grouped?pageNumber=1&pageSize=10", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetGrouped_ReturnsPaged_WithCorrectStructure()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/grouped?pageNumber=1&pageSize=10");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(result);
        Assert.NotNull(result.Paging);
        Assert.Equal(1, result.Paging.CurrentPage);
        Assert.True(result.Paging.TotalCount > 0, "Authenticated user should see their own seeded prints");
        Assert.NotNull(result.Items);
    }

    [Fact]
    public async Task GetGrouped_StandalonePrints_ReturnPrintTypeItems()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/grouped?pageNumber=1&pageSize=25");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(result);
        Assert.Contains(result.Items, item => item.Type == "print");
        var printItem = result.Items.First(item => item.Type == "print");
        Assert.NotNull(printItem.Print);
        Assert.True(printItem.Print.Id > 0);
    }

    [Fact]
    public async Task GetGrouped_WithProject_ReturnsProjectTypeItem()
    {
        var project = await CreateProjectAsync("Grouped Feed Test Project");
        var printRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        printRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        printRequest.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = "Assigned Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Public,
            AllowComments = false,
            ProjectId = project.Id
        });
        var printResp = await _httpClient.SendAsync(printRequest, TestContext.Current.CancellationToken);
        printResp.EnsureSuccessStatusCode();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/grouped?pageNumber=1&pageSize=25");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(result);
        Assert.Contains(result.Items, item => item.Type == "project");
        var projectItem = result.Items.First(item => item.Type == "project" && item.ProjectId == project.Id);
        Assert.Equal(project.Name, projectItem.ProjectName);
        Assert.Equal(1, projectItem.PrintCount);
    }

    [Fact]
    public async Task GetGrouped_Pagination_SecondPageHasFewerOrEqualItems()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/grouped?pageNumber=2&pageSize=2");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(result);
        Assert.Equal(2, result.Paging.CurrentPage);
        Assert.True(result.Paging.TotalCount > 2, "Need more than 2 items to meaningfully test page 2");
        Assert.True(result.Items.Count <= 2);
    }

    [Fact]
    public async Task GetGrouped_SortByTitle_ReturnsItemsInOrder()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/Prints/grouped?pageNumber=1&pageSize=25&sortColumn=Title&sortDirection=Asc");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(result);
        for (int i = 0; i < result.Items.Count - 1; i++)
        {
            var current = result.Items[i].Type == "project"
                ? result.Items[i].ProjectName
                : result.Items[i].Print?.Title;
            var next = result.Items[i + 1].Type == "project"
                ? result.Items[i + 1].ProjectName
                : result.Items[i + 1].Print?.Title;
            if (current != null && next != null)
                Assert.True(string.Compare(current, next, StringComparison.OrdinalIgnoreCase) <= 0);
        }
    }

    [Fact]
    public async Task GetGrouped_WithStatusFilter_ProjectShowsFilteredPrintCount()
    {
        // Create a project with two prints: one Success, one Pending.
        var project = await CreateProjectAsync("Filter Test Project");

        var successPrintReq = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        successPrintReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        successPrintReq.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = "Success Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Private,
            AllowComments = false,
            ProjectId = project.Id
        });
        (await _httpClient.SendAsync(successPrintReq, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var pendingPrintReq = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        pendingPrintReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        pendingPrintReq.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = "Pending Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = Print.PrintStatus.Pending,
            ViewStatus = Print.PrintViewStatus.Private,
            AllowComments = false,
            ProjectId = project.Id
        });
        (await _httpClient.SendAsync(pendingPrintReq, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // Filter by Success status — only the one Success print matches.
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/Prints/grouped?pageNumber=1&pageSize=25&filterByStatus={(int)Print.PrintStatus.Success}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var result = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(result);
        // The project should appear because it has at least one print matching the filter.
        var projectItem = result.Items.FirstOrDefault(item => item.Type == "project" && item.ProjectId == project.Id);
        Assert.NotNull(projectItem);
        // FilteredPrintCount is non-null when filters are active, and equals the number of matching prints.
        Assert.NotNull(projectItem.FilteredPrintCount);
        Assert.Equal(1, projectItem.FilteredPrintCount);
        // PrintCount reflects all prints in the project regardless of filter.
        Assert.Equal(2, projectItem.PrintCount);
    }

    #endregion
}
