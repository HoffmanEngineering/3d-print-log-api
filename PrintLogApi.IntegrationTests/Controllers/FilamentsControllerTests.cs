using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using PrintLogApi.Enums;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
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
            var response = await _httpClient.SendAsync(request);

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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
            var response = await _httpClient.SendAsync(request);

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

            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(model);
            Assert.All(model.Items, f =>
            {
                Assert.NotNull(f.Colors);
                Assert.NotEmpty(f.Colors);
                Assert.Equal(f.ColorHex, f.Colors[0]);
            });
        }

        #endregion

        #region GET Single Filament (Read)

        [Fact]
        public async Task GetFilamentById_Authenticated_ReturnsSuccess()
        {
            // Arrange - first get a filament ID from the summary
            var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
            summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var summaryResponse = await _httpClient.SendAsync(summaryRequest);
            var summary = await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();
            var filamentId = summary.Items.First().Id;

            // Act
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{filamentId}");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetFilamentById_Authenticated_ReturnsExpectedData()
        {
            // Arrange - find the Hatchbox PLA filament
            var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
            summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var summaryResponse = await _httpClient.SendAsync(summaryRequest);
            var summary = await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();
            var hatchboxFilament = summary.Items.First(f => f.Brand == "Hatchbox");

            // Act
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{hatchboxFilament.Id}");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var response = await _httpClient.SendAsync(request);
            var filament = await response.Content.ReadFromJsonAsync<FilamentDetailDto>();

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
            var summaryResponse = await _httpClient.SendAsync(summaryRequest);
            var summary = await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();
            var filamentId = summary.Items.First().Id;

            // Act - no auth header
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{filamentId}");
            var response = await _httpClient.SendAsync(request);

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
            var response = await _httpClient.SendAsync(request);

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
            var response = await _httpClient.SendAsync(request);

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
            var response = await _httpClient.SendAsync(request);
            var createdFilament = await response.Content.ReadFromJsonAsync<FilamentDetailDto>();

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
            var response = await _httpClient.SendAsync(request);

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

            var response = await _httpClient.SendAsync(request);
            var created = await response.Content.ReadFromJsonAsync<FilamentDetailDto>();

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
            var createResponse = await _httpClient.SendAsync(createRequest);
            var createdFilament = await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>();

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
            var updateResponse = await _httpClient.SendAsync(updateRequest);

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
            var createResponse = await _httpClient.SendAsync(createRequest);
            var createdFilament = await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>();

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
            var updateResponse = await _httpClient.SendAsync(updateRequest);
            var updatedFilament = await updateResponse.Content.ReadFromJsonAsync<FilamentDetailDto>();

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
            var summaryResponse = await _httpClient.SendAsync(summaryRequest);
            var summary = await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();
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
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task UpdateFilament_IdMismatch_ReturnsBadRequest()
        {
            // Arrange - get a valid filament ID
            var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
            summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var summaryResponse = await _httpClient.SendAsync(summaryRequest);
            var summary = await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();
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
            var response = await _httpClient.SendAsync(request);

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
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
            var createResponse = await _httpClient.SendAsync(createRequest);
            var createdFilament = await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>();

            // Act
            var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{createdFilament.Id}");
            deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var deleteResponse = await _httpClient.SendAsync(deleteRequest);

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
            var createResponse = await _httpClient.SendAsync(createRequest);
            var createdFilament = await createResponse.Content.ReadFromJsonAsync<FilamentDetailDto>();

            // Act - delete the filament
            var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{createdFilament.Id}");
            deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            await _httpClient.SendAsync(deleteRequest);

            // Assert - try to get the deleted filament
            var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/Filaments/{createdFilament.Id}");
            getRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var getResponse = await _httpClient.SendAsync(getRequest);
            Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        }

        [Fact]
        public async Task DeleteFilament_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - get a valid filament ID first
            var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
            summaryRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var summaryResponse = await _httpClient.SendAsync(summaryRequest);
            var summary = await summaryResponse.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();
            var filamentId = summary.Items.First().Id;

            // Act - no auth header
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{filamentId}");
            var response = await _httpClient.SendAsync(request);

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
            var response = await _httpClient.SendAsync(request);

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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
            var response = await _httpClient.SendAsync(request);
            var model = await response.Content.ReadFromJsonAsync<PagedList<FilamentSummaryDto>>();

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

            var response = await _httpClient.SendAsync(request);
            var filament = await response.Content.ReadFromJsonAsync<FilamentDetailDto>();

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

            var response = await _httpClient.SendAsync(createRequest);
            var created = await response.Content.ReadFromJsonAsync<FilamentDetailDto>();

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

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetFilamentStorageLocations_NotAuthenticated_ReturnsUnauthorized()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/storage-locations");

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetFilamentPurchaseLocations_Authenticated_ReturnsSuccess()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/purchase-locations");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetFilamentPurchaseLocations_NotAuthenticated_ReturnsUnauthorized()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/purchase-locations");

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetFilamentBrands_Authenticated_ReturnsSuccess()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/brands");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task GetFilamentBrands_NotAuthenticated_ReturnsUnauthorized()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments/brands");

            var response = await _httpClient.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion
    }
}
