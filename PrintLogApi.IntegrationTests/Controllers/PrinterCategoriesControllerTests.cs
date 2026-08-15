using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.PrinterCategory;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    /// <summary>
    /// Integration tests for the PrinterCategoriesController.
    /// Tests retrieval of printer categories (FDM, SLA, etc.).
    /// </summary>
    public class PrinterCategoriesControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private readonly CustomWebApplicationFactory _factory;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public PrinterCategoriesControllerTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
            EnsureTestDataExists();
        }

        #region Helper Methods

        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            return request;
        }

        private void EnsureTestDataExists()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            if (!db.PrinterCategories.Any())
            {
                // First ensure material category exists (use lowercase to match existing data convention)
                if (!db.MaterialCategories.Any(mc => mc.Nickname == "filament"))
                {
                    db.MaterialCategories.Add(new MaterialCategory
                    {
                        Nickname = "filament",
                        Name = "Filament",
                        Description = "Thermoplastic filament materials"
                    });
                    db.SaveChanges();
                }

                db.PrinterCategories.AddRange(new[]
                {
                    new PrinterCategory
                    {
                        Nickname = "FDM",
                        Name = "Fused Deposition Modeling",
                        Description = "Extrusion-based 3D printing",
                        MaterialCategoryNickname = "filament",
                        ShowNozzleDiameter = true,
                        ShowFilamentDiameter = true,
                        ShowBedSize = true,
                        ShowHasHeatedBed = true,
                        ShowHasHeatedChamber = true
                    },
                    new PrinterCategory
                    {
                        Nickname = "SLA",
                        Name = "Stereolithography",
                        Description = "Resin-based 3D printing using UV light",
                        ShowScreenResolution = true,
                        ShowBedSize = true
                    }
                });
                db.SaveChanges();
            }
        }

        #endregion

        #region GET /api/PrinterCategories Tests

        [Fact]
        public async Task GetPrinterCategories_WithAuthentication_ReturnsOk()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/PrinterCategories");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<List<PrinterCategoryDto>>(JsonOptions))!;
            Assert.NotNull(result);
            Assert.NotEmpty(result);
        }

        [Fact]
        public async Task GetPrinterCategories_WithoutAuthentication_ReturnsUnauthorized()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/PrinterCategories");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task GetPrinterCategories_ReturnsExpectedFields()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/PrinterCategories");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<List<PrinterCategoryDto>>(JsonOptions))!;
            Assert.NotNull(result);

            var fdm = result.FirstOrDefault(c => c.Nickname == "FDM");
            Assert.NotNull(fdm);
            Assert.Equal("Fused Deposition Modeling", fdm.Name);
            Assert.True(fdm.ShowNozzleDiameter);
            Assert.True(fdm.ShowFilamentDiameter);
            Assert.True(fdm.ShowBedSize);
        }

        [Fact]
        public async Task GetPrinterCategories_ReturnsOrderedByNickname()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/PrinterCategories");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<List<PrinterCategoryDto>>(JsonOptions))!;
            Assert.NotNull(result);

            if (result.Count > 1)
            {
                var nicknames = result.Select(c => c.Nickname).ToList();
                var sortedNicknames = nicknames.OrderBy(n => n).ToList();
                Assert.Equal(sortedNicknames, nicknames);
            }
        }

        [Fact]
        public async Task GetPrinterCategories_IncludesMaterialCategory()
        {
            // Arrange
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/api/PrinterCategories");

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = (await response.Content.ReadFromJsonAsync<List<PrinterCategoryDto>>(JsonOptions))!;
            Assert.NotNull(result);

            var fdm = result.FirstOrDefault(c => c.Nickname == "FDM");
            Assert.NotNull(fdm);
            Assert.NotNull(fdm.MaterialCategory);
            Assert.Equal("filament", fdm.MaterialCategory.Nickname, ignoreCase: true);
        }

        #endregion
    }
}
