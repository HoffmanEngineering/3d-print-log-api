using System.Net;
using PrintLogApi.Enums;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

public class FilamentsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public FilamentsControllerTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    #region GET Summary Tests (GetFilamentSummariesForUser)

    [Fact]
    public async Task GetFilamentSummaries_Authenticated_ReturnsSuccess()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentSummaries_Authenticated_ReturnsPagedResult()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(model);
        Assert.NotNull(model.Paging);
        Assert.Equal(1, model.Paging.CurrentPage);
        Assert.True(model.Paging.TotalCount >= 3, "Should have at least 3 seeded filaments");
    }

    [Fact]
    public async Task GetFilamentSummaries_Authenticated_ReturnsFilamentsWithExpectedData()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(model);
        Assert.NotEmpty(model.Items);

        // Check that seeded filaments exist
        Assert.True(model.Items.Any(f => f.Brand == "Hatchbox"),
            "Should contain Hatchbox filament");
        Assert.True(model.Items.Any(f => f.Brand == "Prusament"),
            "Should contain Prusament filament");
        Assert.True(model.Items.Any(f => f.Brand == "eSUN"),
            "Should contain eSUN filament");

        // Verify filament structure
        var anyFilament = model.Items.First();
        Assert.NotEqual(Guid.Empty, anyFilament.Id);
        Assert.NotNull(anyFilament.DisplayName);
    }

    [Fact]
    public async Task GetFilamentSummaries_Authenticated_WithPagination_ReturnsPaginatedResult()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments?pageSize=1");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(model);
        Assert.NotNull(model.Paging);
        Assert.Single(model.Items);
        Assert.True(model.Paging.TotalCount >= 3, "Total count should reflect all filaments");
    }

    [Fact]
    public async Task GetFilamentSummaries_Authenticated_WithSearchText_FiltersResults()
    {
        // Arrange - search for "PLA" (seeded filament material type)
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments?searchText=PLA");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(model);
        Assert.NotEmpty(model.Items);
        Assert.All(model.Items, f =>
            Assert.True(
                (f.MaterialType != null && f.MaterialType.Contains("PLA")) ||
                (f.DisplayName != null && f.DisplayName.Contains("PLA")) ||
                (f.Brand != null && f.Brand.Contains("PLA")),
                $"Filament should match search term 'PLA': {f.DisplayName}"));
    }

    [Fact]
    public async Task GetFilamentSummaries_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentSummaries_LegacyRecord_ColorsDefaultsToColorHex()
    {
        // The seeded filaments have no Colors set (legacy rows) — the summary DTO
        // should fall back to Colors=[ColorHex] for each
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.All(model.Items, f =>
        {
            Assert.NotNull(f.Colors);
            Assert.NotEmpty(f.Colors);
            Assert.Equal(f.ColorHex, f.Colors[0]);
        });
    }

    [Fact]
    public async Task GetFilamentSummaries_FilterByColorPattern_ReturnsMatchingOnly()
    {
        // Seed a Rainbow filament so we have something to filter on
        var rainbowFilament = new AddFilamentDto
        {
            DisplayName = "Rainbow Filter Test",
            Brand = "FilterTestBrand",
            MaterialType = "PLA",
            ColorHex = "FF0000",
            Colors = new List<string> { "FF0000", "00FF00", "0000FF" },
            ColorPattern = ColorPatternType.Rainbow,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createReq.Content = JsonContent.Create(rainbowFilament);
        await _httpClient.SendAsync(createReq, TestContext.Current.CancellationToken);

        // Filter to Rainbow only
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments?colorPatterns={(int)ColorPatternType.Rainbow}&includeInactive=true");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.Paging.TotalCount >= 1);
        Assert.All(model.Items, f => Assert.Equal(ColorPatternType.Rainbow, f.ColorPattern));
    }

    [Fact]
    public async Task GetFilamentSummaries_FilterByEffect_ReturnsMatchingOnly()
    {
        // Seed a filament with Sparkle effect
        var sparkleFilament = new AddFilamentDto
        {
            DisplayName = "Sparkle Filter Test",
            Brand = "FilterTestBrand",
            MaterialType = "PLA",
            ColorHex = "FFD700",
            Colors = new List<string> { "FFD700" },
            ColorPattern = ColorPatternType.Solid,
            Effects = new List<FilamentEffect> { FilamentEffect.Sparkle },
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createReq.Content = JsonContent.Create(sparkleFilament);
        await _httpClient.SendAsync(createReq, TestContext.Current.CancellationToken);

        // Filter to Sparkle effect
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments?effects={(int)FilamentEffect.Sparkle}&includeInactive=true");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.Paging.TotalCount >= 1);
        Assert.All(model.Items, f => Assert.Contains(FilamentEffect.Sparkle, f.Effects));
    }

    [Fact]
    public async Task GetFilamentSummaries_FilterByFinishType_ReturnsMatchingOnly()
    {
        // Seed a Silk finish filament
        var silkFilament = new AddFilamentDto
        {
            DisplayName = "Silk Filter Test",
            Brand = "FilterTestBrand",
            MaterialType = "PLA",
            ColorHex = "C0C0C0",
            Colors = new List<string> { "C0C0C0" },
            ColorPattern = ColorPatternType.Solid,
            FinishType = FilamentFinishType.Silk,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createReq.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createReq.Content = JsonContent.Create(silkFilament);
        await _httpClient.SendAsync(createReq, TestContext.Current.CancellationToken);

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Filaments?finishTypes={(int)FilamentFinishType.Silk}&includeInactive=true");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.Paging.TotalCount >= 1);
        Assert.All(model.Items, f => Assert.Equal(FilamentFinishType.Silk, f.FinishType));
    }

    #endregion

    #region GET Single Filament (Read)

    [Fact]
    public async Task GetFilamentById_Authenticated_ReturnsSuccess()
    {
        // Arrange - first get a filament ID from the summary
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var filamentId = summary.Items.First().Id;

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{filamentId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentById_Authenticated_ReturnsExpectedData()
    {
        // Arrange - find the Hatchbox PLA filament
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var hatchboxFilament = summary.Items.First(f => f.Brand == "Hatchbox");

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{hatchboxFilament.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var filament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(filament);
        Assert.Equal(hatchboxFilament.Id, filament.Id);
        Assert.Equal("Hatchbox", filament.Brand);
        Assert.Equal("PLA", filament.MaterialType);
        Assert.Equal("FF0000", filament.ColorHex);
        Assert.Equal(1.75, filament.DiameterMm);
        Assert.True(filament.IsActive);
    }

    [Fact]
    public async Task GetFilamentById_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - get a valid filament ID first
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var filamentId = summary.Items.First().Id;

        // Act - no auth header
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{filamentId}");
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentById_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{nonExistentId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region POST Filament (Create)

    [Fact]
    public async Task CreateFilament_Authenticated_ReturnsCreated()
    {
        // Arrange
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Integration Test Filament",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Green",
            ColorHex = "00FF00",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1000000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newFilament);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateFilament_Authenticated_ReturnsCreatedFilament()
    {
        // Arrange
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Full Test Filament",
            Brand = "Custom Brand",
            MaterialType = "PETG",
            ColorName = "Purple",
            ColorHex = "800080",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 750000,
            MaterialDensityGramPerCubicCm = 1.27,
            IsActive = true,
            TempRangeStart = 220,
            TempRangeEnd = 250,
            RecommendedTemp = 235,
            RecommendedBedTemp = 70,
            Notes = "Test filament with all fields",
            StorageLocation = "Shelf A"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newFilament);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var createdFilament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(createdFilament);
        Assert.NotEqual(Guid.Empty, createdFilament.Id);
        Assert.Equal("Full Test Filament", createdFilament.DisplayName);
        Assert.Equal("Custom Brand", createdFilament.Brand);
        Assert.Equal("PETG", createdFilament.MaterialType);
        Assert.Equal("Purple", createdFilament.ColorName);
        Assert.Equal("800080", createdFilament.ColorHex);
        Assert.Equal(235, createdFilament.RecommendedTemp);
        Assert.Equal(70, createdFilament.RecommendedBedTemp);
    }

    [Fact]
    public async Task CreateFilament_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Should Not Be Created",
            Brand = "Test",
            MaterialType = "PLA",
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Content = JsonContent.Create(newFilament);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateFilament_OldClientSendsColorHexOnly_NormalizesToColorsArray()
    {
        // Old client sends ColorHex but no Colors
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Old Client Filament",
            Brand = "LegacyBrand",
            MaterialType = "PLA",
            ColorHex = "e05c5c",
            Colors = new List<string>(),   // old client does not send Colors
            ColorPattern = null,
            FinishType = null,
            Effects = new List<FilamentEffect>(),
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newFilament);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var created = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(created);
        Assert.Equal(new List<string> { "e05c5c" }, created.Colors);
        Assert.Equal("e05c5c", created.ColorHex);
        Assert.Equal(ColorPatternType.Solid, created.ColorPattern);
        Assert.Equal(FilamentFinishType.Standard, created.FinishType);
    }

    #endregion

    #region PUT Filament (Update)

    [Fact]
    public async Task UpdateFilament_Authenticated_ReturnsSuccess()
    {
        // Arrange - first create a filament to update
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Filament To Update",
            Brand = "Original Brand",
            MaterialType = "PLA",
            ColorName = "White",
            ColorHex = "FFFFFF",
            DiameterMm = 1.75,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdFilament = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Arrange - prepare update (using FilamentDetailDto for PUT)
        var updateDto = new FilamentDetailDto
        {
            Id = createdFilament.Id,
            DisplayName = "Updated Filament Name",
            Brand = "Updated Brand",
            MaterialType = "PETG",
            ColorName = "Blue",
            ColorHex = "0000FF",
            DiameterMm = 1.75,
            IsActive = true
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{createdFilament.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        var updateResponse = await _httpClient.SendAsync(updateRequest, TestContext.Current.CancellationToken);

        // Assert - PUT endpoint returns CreatedAtAction (201)
        Assert.Equal(HttpStatusCode.Created, updateResponse.StatusCode);
    }

    [Fact]
    public async Task UpdateFilament_Authenticated_ReturnsUpdatedData()
    {
        // Arrange - first create a filament to update
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Filament For Update Test",
            Brand = "Original",
            MaterialType = "PLA",
            ColorName = "Red",
            ColorHex = "FF0000",
            DiameterMm = 1.75,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
            Notes = "Original notes"
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdFilament = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Arrange - prepare update with changed fields
        var updateDto = new FilamentDetailDto
        {
            Id = createdFilament.Id,
            DisplayName = "Completely Updated Filament",
            Brand = "New Brand",
            MaterialType = "ABS",
            ColorName = "Yellow",
            ColorHex = "FFFF00",
            DiameterMm = 2.85,
            IsActive = false,
            Notes = "Updated notes",
            RecommendedTemp = 240,
            RecommendedBedTemp = 100
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{createdFilament.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        var updateResponse = await _httpClient.SendAsync(updateRequest, TestContext.Current.CancellationToken);
        var updatedFilament = (await updateResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Equal(HttpStatusCode.Created, updateResponse.StatusCode);
        Assert.NotNull(updatedFilament);
        Assert.Equal("Completely Updated Filament", updatedFilament.DisplayName);
        Assert.Equal("New Brand", updatedFilament.Brand);
        Assert.Equal("ABS", updatedFilament.MaterialType);
        Assert.Equal("Yellow", updatedFilament.ColorName);
        Assert.Equal("FFFF00", updatedFilament.ColorHex);
        Assert.Equal(2.85, updatedFilament.DiameterMm);
        Assert.False(updatedFilament.IsActive);
        Assert.Equal("Updated notes", updatedFilament.Notes);
    }

    [Fact]
    public async Task UpdateFilament_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - get a valid filament ID first
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var filamentId = summary.Items.First().Id;

        var updateDto = new FilamentDetailDto
        {
            Id = filamentId,
            DisplayName = "Should Not Update",
            Brand = "Test",
            MaterialType = "PLA",
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{filamentId}");
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateFilament_IdMismatch_ReturnsBadRequest()
    {
        // Arrange - get a valid filament ID
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var filamentId = summary.Items.First().Id;

        // ID in DTO doesn't match route ID
        var updateDto = new FilamentDetailDto
        {
            Id = Guid.NewGuid(), // Mismatched ID
            DisplayName = "Updated Name",
            Brand = "Test",
            MaterialType = "PLA",
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{filamentId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateFilament_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var updateDto = new FilamentDetailDto
        {
            Id = nonExistentId,
            DisplayName = "Non-existent Filament",
            Brand = "Test",
            MaterialType = "PLA",
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{nonExistentId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateFilament_OldClientSendsColorHexOnly_NormalizesToColorsArray()
    {
        // Create a filament first
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Filament For Update Normalization Test",
            Brand = "TestBrand",
            MaterialType = "PLA",
            ColorHex = "FF0000",
            Colors = new List<string> { "FF0000" },
            ColorPattern = ColorPatternType.Solid,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Old client sends update with only ColorHex (no Colors)
        var updateDto = new FilamentDetailDto
        {
            Id = created.Id,
            DisplayName = created.DisplayName,
            Brand = created.Brand,
            MaterialType = created.MaterialType,
            ColorHex = "0000FF",        // old client changes color via ColorHex
            Colors = new List<string>(), // old client sends empty Colors
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{created.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        var updateResponse = await _httpClient.SendAsync(updateRequest, TestContext.Current.CancellationToken);
        var updated = (await updateResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.Created, updateResponse.StatusCode);
        Assert.NotNull(updated);
        Assert.Equal(new List<string> { "0000FF" }, updated.Colors);
        Assert.Equal("0000FF", updated.ColorHex);
        Assert.Equal(ColorPatternType.Solid, updated.ColorPattern);
        Assert.Equal(FilamentFinishType.Standard, updated.FinishType);
    }

    #endregion

    #region DELETE Filament

    [Fact]
    public async Task DeleteFilament_Authenticated_ReturnsNoContent()
    {
        // Arrange - first create a filament to delete (one without prints)
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Filament To Delete",
            Brand = "Delete Test",
            MaterialType = "PLA",
            ColorName = "Gray",
            ColorHex = "808080",
            DiameterMm = 1.75,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdFilament = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{createdFilament.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var deleteResponse = await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteFilament_Authenticated_FilamentNoLongerExists()
    {
        // Arrange - first create a filament to delete
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Filament To Delete And Verify",
            Brand = "Delete Test",
            MaterialType = "PLA",
            ColorName = "Orange",
            ColorHex = "FFA500",
            DiameterMm = 1.75,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var createdFilament = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act - delete the filament
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{createdFilament.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        await _httpClient.SendAsync(deleteRequest, TestContext.Current.CancellationToken);

        // Assert - try to get the deleted filament
        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{createdFilament.Id}");
        getRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var getResponse = await _httpClient.SendAsync(getRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteFilament_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - get a valid filament ID first
        var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var summaryResponse = await _httpClient.SendAsync(summaryRequest, TestContext.Current.CancellationToken);
        var summary = (await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;
        var filamentId = summary.Items.First().Id;

        // Act - no auth header
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{filamentId}");
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteFilament_NonExistent_ReturnsForbidden()
    {
        // Arrange - API checks ownership before existence, so returns Forbidden for non-existent
        var nonExistentId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{nonExistentId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    #endregion

    #region Filter by Storage Location

    [Fact]
    public async Task GetFilamentSummaries_FilterByStorageLocation_ReturnsOnlyMatchingFilaments()
    {
        // Arrange - filaments 1 and 2 are seeded with TestStorageLocation; filament 3 has none
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Filaments?filterByStorageLocation={Uri.EscapeDataString(IntegrationTestSeeder.TestStorageLocation)}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.Items.Any(f => f.Brand == "Hatchbox"),
            "Hatchbox (storage location set) should be included");
        Assert.True(model.Items.Any(f => f.Brand == "Prusament"),
            "Prusament (storage location set) should be included");
        Assert.DoesNotContain(model.Items, f => f.Brand == "eSUN");
        Assert.All(model.Items, f =>
            Assert.Equal(IntegrationTestSeeder.TestStorageLocation, f.StorageLocation));
    }

    [Fact]
    public async Task GetFilamentSummaries_FilterByStorageLocation_Unassigned_ReturnsOnlyUnassignedFilaments()
    {
        // Arrange - filament 3 has no storage location; filaments 1 and 2 do
        var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/Filaments?filterByStorageLocation=__unassigned__");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.Items.Any(f => f.Brand == "eSUN"),
            "eSUN (no storage location) should be included");
        Assert.DoesNotContain(model.Items, f => f.Brand == "Hatchbox");
        Assert.DoesNotContain(model.Items, f => f.Brand == "Prusament");
        Assert.All(model.Items, f =>
            Assert.True(f.StorageLocation == null || f.StorageLocation == "",
                $"All results should have no storage location, but got: {f.StorageLocation}"));
    }

    [Fact]
    public async Task GetFilamentSummaries_FilterByStorageLocation_NonExistent_ReturnsEmpty()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/Filaments?filterByStorageLocation=DoesNotExist");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.Empty(model.Items);
        Assert.Equal(0, model.Paging.TotalCount);
    }

    [Fact]
    public async Task GetFilamentSummaries_NoStorageLocationFilter_ReturnsAllFilaments()
    {
        // Arrange - no filter param
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(model);
        Assert.True(model.Paging.TotalCount >= 3,
            "All seeded filaments should be returned when no filter is applied");
    }

    #endregion

    #region Multi-Color Filament Fields

    [Fact]
    public async Task GetFilamentById_LegacyRecord_ColorsDefaultsToColorHex()
    {
        // The seeded Hatchbox filament has ColorHex="FF0000" but no Colors set (legacy)
        // Reading it should return Colors=["FF0000"] and ColorPattern=Solid
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Filaments/{IntegrationTestSeeder.TestFilamentId1}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var filament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(filament);
        Assert.Equal(new List<string> { "FF0000" }, filament.Colors);
        Assert.Equal("FF0000", filament.ColorHex);
        Assert.Equal(ColorPatternType.Solid, filament.ColorPattern);
        Assert.Equal(FilamentFinishType.Standard, filament.FinishType);
        Assert.Empty(filament.Effects);
    }

    [Fact]
    public async Task CreateFilament_WithMultiColor_StoresAndReturnsColors()
    {
        // Create a new filament with multi-color data
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Multi-Color Test Filament",
            Brand = "TestBrand",
            MaterialType = "PLA",
            ColorHex = "FF0000",
            Colors = new List<string> { "FF0000", "0000FF" },
            ColorPattern = ColorPatternType.Multi,
            FinishType = FilamentFinishType.Silk,
            Effects = new List<FilamentEffect> { FilamentEffect.Sparkle },
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);

        var response = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(created);
        Assert.Equal(new List<string> { "FF0000", "0000FF" }, created.Colors);
        Assert.Equal("FF0000", created.ColorHex);
        Assert.Equal(ColorPatternType.Multi, created.ColorPattern);
        Assert.Equal(FilamentFinishType.Silk, created.FinishType);
        Assert.Equal(new List<FilamentEffect> { FilamentEffect.Sparkle }, created.Effects);
    }

    #endregion

    #region GET Storage/Purchase Locations and Brands

    [Fact]
    public async Task GetFilamentStorageLocations_Authenticated_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/storage-locations");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentStorageLocations_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/storage-locations");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentPurchaseLocations_Authenticated_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/purchase-locations");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentPurchaseLocations_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/purchase-locations");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentBrands_Authenticated_ReturnsSuccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/brands");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentBrands_NotAuthenticated_ReturnsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/brands");

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region GET Detail - FilamentRemaining

    [Fact]
    public async Task GetFilamentById_ReturnsFilamentRemaining_WhenNominalWeightPresent()
    {
        // Arrange - create a filament with a known nominal weight and no prints/adjustments
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Remaining Test Filament",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Green",
            ColorHex = "00FF00",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1000000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{created.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var filament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(filament);
        Assert.Equal(1000000, filament.FilamentRemaining);
    }

    [Fact]
    public async Task GetFilamentById_FilamentRemaining_NullWhenNominalWeightMissing()
    {
        // Arrange - create a filament WITHOUT a nominal weight
        var newFilament = new AddFilamentDto
        {
            DisplayName = "No Nominal Weight Filament",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Blue",
            ColorHex = "0000FF",
            DiameterMm = 1.75,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{created.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var filament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(filament);
        Assert.Null(filament.FilamentRemaining);
    }

    [Fact]
    public async Task GetFilamentById_FilamentRemaining_ReflectsWeightAdjustments()
    {
        // Arrange - create a filament, then add a -200,000 mg weight adjustment via PUT
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Adjustment Remaining Filament",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Red",
            ColorHex = "FF0000",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1000000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        var updateDto = new FilamentDetailDto
        {
            Id = created.Id,
            DisplayName = created.DisplayName,
            Brand = created.Brand,
            MaterialType = created.MaterialType,
            MaterialCategoryNickname = created.MaterialCategoryNickname,
            ColorName = created.ColorName,
            ColorHex = created.ColorHex,
            DiameterMm = created.DiameterMm,
            InitialNominalWeightMg = created.InitialNominalWeightMg,
            MaterialDensityGramPerCubicCm = created.MaterialDensityGramPerCubicCm,
            IsActive = created.IsActive,
            FilamentAdjustments = new List<FilamentAdjustmentDto>
            {
                new FilamentAdjustmentDto
                {
                    FilamentId = created.Id,
                    Source = FilamentAdjustment.SourceMeasurement.Weight,
                    AmountMg = -200000,
                    Notes = "Measured adjustment"
                }
            }
        };
        var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{created.Id}");
        putRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        putRequest.Content = JsonContent.Create(updateDto);
        await _httpClient.SendAsync(putRequest, TestContext.Current.CancellationToken);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{created.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var filament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert
        Assert.NotNull(filament);
        Assert.Equal(800000, filament.FilamentRemaining);
    }

    [Fact]
    public async Task GetFilamentById_FilamentRemaining_SubtractsPrintUsage()
    {
        // Arrange - create a filament with a known nominal weight
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Print Usage Remaining Filament",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Teal",
            ColorHex = "008080",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1000000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Create a print that uses 200,000 mg (actual weight) of that filament
        var newPrint = new AddPrintDTO
        {
            Title = "Filament Remaining Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            FilamentUsage = new List<PrintFilamentSummaryDto>
            {
                new PrintFilamentSummaryDto
                {
                    Id = Guid.NewGuid(),
                    Filament = new FilamentSummaryDto { Id = created.Id },
                    Source = PrintFilament.SourceMeasurement.Weight,
                    AmountMg = 200000
                }
            }
        };
        var printRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        printRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        printRequest.Content = JsonContent.Create(newPrint);
        await _httpClient.SendAsync(printRequest, TestContext.Current.CancellationToken);

        // Act
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{created.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var filament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Assert - 1,000,000 nominal - 200,000 used = 800,000
        Assert.NotNull(filament);
        Assert.Equal(800000, filament.FilamentRemaining);
    }

    [Fact]
    public async Task PutFilament_IgnoresProvidedFilamentRemaining()
    {
        // Arrange - create a filament with a known nominal weight
        var newFilament = new AddFilamentDto
        {
            DisplayName = "Ignore Remaining Filament",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Cyan",
            ColorHex = "00FFFF",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1000000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        };
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newFilament);
        var createResponse = await _httpClient.SendAsync(createRequest, TestContext.Current.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        // Act - PUT a bogus FilamentRemaining that the server must ignore
        var updateDto = new FilamentDetailDto
        {
            Id = created.Id,
            DisplayName = created.DisplayName,
            Brand = created.Brand,
            MaterialType = created.MaterialType,
            MaterialCategoryNickname = created.MaterialCategoryNickname,
            ColorName = created.ColorName,
            ColorHex = created.ColorHex,
            DiameterMm = created.DiameterMm,
            InitialNominalWeightMg = created.InitialNominalWeightMg,
            MaterialDensityGramPerCubicCm = created.MaterialDensityGramPerCubicCm,
            IsActive = created.IsActive,
            FilamentRemaining = 123456
        };
        var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{created.Id}");
        putRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        putRequest.Content = JsonContent.Create(updateDto);
        await _httpClient.SendAsync(putRequest, TestContext.Current.CancellationToken);

        // Assert - GET still computes remaining from nominal, not the provided value
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{created.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var filament = (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;

        Assert.NotNull(filament);
        Assert.Equal(1000000, filament.FilamentRemaining);
    }

    #endregion
    #region GET Detail - Remaining Length/Volume and Usage Totals

    /// <summary>
    /// Creates a filament owned by the test user and returns the created detail.
    /// Every test here builds its own rows: the seeder creates no PrintFilament or
    /// FilamentAdjustment records, and the class shares one database seeded once, so
    /// mutating a seeded filament would leak into the rest of the file.
    /// </summary>
    private async Task<FilamentDetailDto> CreateUsageTotalsFilamentAsync(string displayName, long? nominalWeightMg = 1000000)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddFilamentDto
        {
            DisplayName = displayName,
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Green",
            ColorHex = "00FF00",
            DiameterMm = 1.75,
            InitialNominalWeightMg = nominalWeightMg,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true
        });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Creates one print that consumes the given filament once per entry in usageMg.
    /// </summary>
    private async Task CreatePrintUsingFilamentAsync(Guid filamentId, params int[] usageMg)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = "Usage Totals Print",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Public,
            AllowComments = true,
            FilamentUsage = usageMg
                .Select(mg => new PrintFilamentSummaryDto
                {
                    Id = Guid.NewGuid(),
                    Filament = new FilamentSummaryDto { Id = filamentId },
                    Source = PrintFilament.SourceMeasurement.Weight,
                    AmountMg = mg
                })
                .ToList<PrintFilamentSummaryDto>()
        });

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Expected 201, got {(int)response.StatusCode}. Body: {body}");
    }

    private async Task<FilamentDetailDto> GetFilamentDetailAsync(Guid id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(cancellationToken: TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task GetFilamentById_WithUsageHistory_ReturnsSuccessNotServerError()
    {
        // Regression guard: a mapped field reached through PrintFilament.Print would
        // dereference an un-Included navigation and 500 here.
        var filament = await CreateUsageTotalsFilamentAsync("Usage History Filament");
        await CreatePrintUsingFilamentAsync(filament.Id, 12000);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{filament.Id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetFilamentById_ReturnsRemainingLengthVolumeAndUsageTotals()
    {
        var filament = await CreateUsageTotalsFilamentAsync("Usage Totals Filament");
        await CreatePrintUsingFilamentAsync(filament.Id, 12000);

        var model = await GetFilamentDetailAsync(filament.Id);

        Assert.NotNull(model.FilamentLengthRemainingInM);
        Assert.NotNull(model.FilamentVolumeRemainingInMl);
        Assert.Equal(1, model.PrintCount);
        Assert.Equal(12000, model.TotalUsedMg);
        Assert.Equal(1000000 - 12000, model.FilamentRemaining);
    }

    [Fact]
    public async Task GetFilamentById_PrintCountIsDistinctPrints_NotUsageRows()
    {
        // A print may hold two PrintFilament rows for one spool: there is no unique index
        // on (PrintId, FilamentId). PrintCount counts prints; TotalUsedMg counts rows.
        var filament = await CreateUsageTotalsFilamentAsync("Duplicate Usage Filament");
        await CreatePrintUsingFilamentAsync(filament.Id, 12000, 30000);

        var model = await GetFilamentDetailAsync(filament.Id);

        Assert.Equal(1, model.PrintCount);
        Assert.Equal(42000, model.TotalUsedMg);
    }

    [Fact]
    public async Task GetFilamentById_UntrackedSpool_ReturnsNullRemainingLengthAndVolume()
    {
        var filament = await CreateUsageTotalsFilamentAsync("Untracked Spool Filament", nominalWeightMg: null);

        var model = await GetFilamentDetailAsync(filament.Id);

        // The whole point of the guard: remaining, length and volume agree that the
        // spool is untracked rather than reporting null alongside a contradictory 0.
        Assert.Null(model.FilamentRemaining);
        Assert.Null(model.FilamentLengthRemainingInM);
        Assert.Null(model.FilamentVolumeRemainingInMl);
    }

    [Fact]
    public async Task GetFilamentById_NoUsage_ReturnsZeroTotals()
    {
        var filament = await CreateUsageTotalsFilamentAsync("Unused Spool Filament");

        var model = await GetFilamentDetailAsync(filament.Id);

        Assert.Equal(0, model.PrintCount);
        Assert.Equal(0, model.TotalUsedMg);
    }

    [Fact]
    public async Task PutFilament_IgnoresClientSuppliedUsageTotals()
    {
        var created = await CreateUsageTotalsFilamentAsync("Ignore Usage Totals Filament");
        await CreatePrintUsingFilamentAsync(created.Id, 12000);

        var updateDto = new FilamentDetailDto
        {
            Id = created.Id,
            DisplayName = created.DisplayName,
            Brand = created.Brand,
            MaterialType = created.MaterialType,
            MaterialCategoryNickname = created.MaterialCategoryNickname,
            ColorName = created.ColorName,
            ColorHex = created.ColorHex,
            DiameterMm = created.DiameterMm,
            InitialNominalWeightMg = created.InitialNominalWeightMg,
            MaterialDensityGramPerCubicCm = created.MaterialDensityGramPerCubicCm,
            IsActive = created.IsActive,
            PrintCount = 9999,
            TotalUsedMg = 123456789,
            FilamentLengthRemainingInM = 42,
            FilamentVolumeRemainingInMl = 42
        };
        var putRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Filaments/{created.Id}");
        putRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        putRequest.Content = JsonContent.Create(updateDto);
        var putResponse = await _httpClient.SendAsync(putRequest, TestContext.Current.CancellationToken);
        Assert.True(putResponse.IsSuccessStatusCode, $"PUT failed: {putResponse.StatusCode}");

        var after = await GetFilamentDetailAsync(created.Id);

        Assert.Equal(1, after.PrintCount);
        Assert.Equal(12000, after.TotalUsedMg);
    }

    #endregion

}
