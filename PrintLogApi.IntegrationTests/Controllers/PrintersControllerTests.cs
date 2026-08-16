using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrintersControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;
    private Guid _filamentId1;
    private Guid _filamentId2;
    private Guid _filamentId3;

    public PrintersControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();

        // Get filament IDs from this instance's database
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var filaments = db.Filaments.OrderBy(f => f.DisplayName).Take(3).ToList();
        if (filaments.Count >= 3)
        {
            _filamentId1 = filaments[0].Id;
            _filamentId2 = filaments[1].Id;
            _filamentId3 = filaments[2].Id;
        }
    }

    #region GET Summary Tests

    [Fact]
    public async Task GetPrinterSummary_Authenticated_ReturnsSuccess()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/summary");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrinterSummary_Authenticated_ReturnsPagedResult()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/summary");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrinterSummaryWithFilamentDto>>())!;

        // Assert
        Assert.NotNull(model);
        Assert.NotNull(model.Paging);
        Assert.Equal(1, model.Paging.CurrentPage);
        Assert.True(model.Paging.TotalCount >= 2, "Should have at least 2 seeded printers");
    }

    [Fact]
    public async Task GetPrinterSummary_Authenticated_ReturnsPrintersWithExpectedData()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/summary");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrinterSummaryWithFilamentDto>>())!;

        // Assert
        Assert.NotNull(model);
        Assert.NotEmpty(model.Items);

        // Check that seeded test printers exist
        Assert.True(model.Items.Any(p => p.Name!.Contains("Test Printer")),
            "Should contain at least one seeded Test Printer");

        // Verify printer structure
        var anyPrinter = model.Items.First();
        Assert.True(anyPrinter.Id > 0);
        Assert.NotNull(anyPrinter.Name);
        Assert.NotNull(anyPrinter.Make);
        Assert.NotNull(anyPrinter.Model);
    }

    [Fact]
    public async Task GetPrinterSummary_Authenticated_WithPagination_ReturnsPaginatedResult()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/summary?pageSize=1");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrinterSummaryWithFilamentDto>>())!;

        // Assert
        Assert.NotNull(model);
        Assert.NotNull(model.Paging);
        Assert.Single(model.Items);
        Assert.True(model.Paging.TotalCount >= 2, "Total count should reflect all printers");
    }

    [Fact]
    public async Task GetPrinterSummary_Authenticated_WithSearchText_FiltersResults()
    {
        // Arrange - search for "Creality" (seeded printer make)
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/summary?searchText=Creality");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var model = (await response.Content.ReadFromJsonAsync<PagedList<PrinterSummaryWithFilamentDto>>())!;

        // Assert
        Assert.NotNull(model);
        Assert.NotEmpty(model.Items);
        Assert.All(model.Items, p =>
            Assert.True(p.Make!.Contains("Creality") || p.Name!.Contains("Creality") || p.Model!.Contains("Creality")));
    }

    [Fact]
    public async Task GetPrinterSummary_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/summary");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region GET Single Printer (Read)

    [Fact]
    public async Task GetPrinterById_Authenticated_ReturnsSuccess()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetPrinterById_Authenticated_ReturnsExpectedData()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var printer = (await response.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Assert
        Assert.NotNull(printer);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId, printer.Id);
        Assert.Equal("Test Printer 1", printer.Name);
        Assert.Equal("Creality", printer.Make);
        Assert.Equal("Ender 3", printer.Model);
        Assert.True(printer.IsActive);
    }

    [Fact]
    public async Task GetPrinterById_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPrinterById_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region POST Printer (Create)

    [Fact]
    public async Task CreatePrinter_Authenticated_ReturnsCreated()
    {
        // Arrange
        var newPrinter = new AddPrinterDTO
        {
            Name = "Integration Test Printer",
            Make = "Test Make",
            Model = "Test Model",
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrinter);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreatePrinter_Authenticated_ReturnsCreatedPrinter()
    {
        // Arrange
        var newPrinter = new AddPrinterDTO
        {
            Name = "Full Test Printer",
            Make = "Custom Make",
            Model = "Custom Model",
            Description = "A test printer with all fields",
            IsActive = true,
            NozzleDiameter = 0.4,
            FilamentDiameter = 1.75,
            BedWidthMm = 220,
            BedHeightMm = 220,
            BedDepthMm = 250,
            HasHeatedBed = true,
            HasHeatedChamber = false
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrinter);

        // Act
        var response = await _httpClient.SendAsync(request);
        var createdPrinter = (await response.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(createdPrinter);
        Assert.True(createdPrinter.Id > 0);
        Assert.Equal("Full Test Printer", createdPrinter.Name);
        Assert.Equal("Custom Make", createdPrinter.Make);
        Assert.Equal("Custom Model", createdPrinter.Model);
        Assert.Equal("A test printer with all fields", createdPrinter.Description);
        Assert.Equal(0.4, createdPrinter.NozzleDiameter);
        Assert.Equal(1.75, createdPrinter.FilamentDiameter);
        Assert.True(createdPrinter.HasHeatedBed);
        Assert.False(createdPrinter.HasHeatedChamber);
    }

    [Fact]
    public async Task CreatePrinter_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var newPrinter = new AddPrinterDTO
        {
            Name = "Should Not Be Created",
            Make = "Test",
            Model = "Test"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Content = JsonContent.Create(newPrinter);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreatePrinter_WithMissingRequiredFields_ReturnsBadRequest()
    {
        // Arrange - Name, Make, and Model are required
        var newPrinter = new AddPrinterDTO
        {
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrinter);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region PUT Printer (Update)

    [Fact]
    public async Task UpdatePrinter_Authenticated_ReturnsSuccess()
    {
        // Arrange - first create a printer to update
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer To Update",
            Make = "Original Make",
            Model = "Original Model",
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Arrange - prepare update
        var updateDto = new AddPrinterDTO
        {
            Id = createdPrinter.Id,
            Name = "Updated Printer Name",
            Make = "Updated Make",
            Model = "Updated Model",
            IsActive = true
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        var updateResponse = await _httpClient.SendAsync(updateRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
    }

    [Fact]
    public async Task UpdatePrinter_Authenticated_ReturnsUpdatedData()
    {
        // Arrange - first create a printer to update
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer For Update Test",
            Make = "Original",
            Model = "Original",
            IsActive = true,
            Description = "Original description"
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Arrange - prepare update with all fields changed
        var updateDto = new AddPrinterDTO
        {
            Id = createdPrinter.Id,
            Name = "Completely Updated Printer",
            Make = "New Make",
            Model = "New Model",
            Description = "Updated description",
            IsActive = false,
            NozzleDiameter = 0.6,
            FilamentDiameter = 2.85,
            BedWidthMm = 300,
            BedHeightMm = 300,
            BedDepthMm = 400
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        var updateResponse = await _httpClient.SendAsync(updateRequest);
        var updatedPrinter = (await updateResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Assert
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.NotNull(updatedPrinter);
        Assert.Equal("Completely Updated Printer", updatedPrinter.Name);
        Assert.Equal("New Make", updatedPrinter.Make);
        Assert.Equal("New Model", updatedPrinter.Model);
        Assert.Equal("Updated description", updatedPrinter.Description);
        Assert.False(updatedPrinter.IsActive);
        Assert.Equal(0.6, updatedPrinter.NozzleDiameter);
        Assert.Equal(2.85, updatedPrinter.FilamentDiameter);
    }

    [Fact]
    public async Task UpdatePrinter_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange
        var updateDto = new AddPrinterDTO
        {
            Id = IntegrationTestSeeder.TestPrinterId,
            Name = "Should Not Update",
            Make = "Test",
            Model = "Test",
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}");
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePrinter_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var updateDto = new AddPrinterDTO
        {
            Id = 999999,
            Name = "Non-existent Printer",
            Make = "Test",
            Model = "Test",
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/Printers/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(updateDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdatePrinter_IdMismatch_ReturnsBadRequest()
    {
        var updateDto = new AddPrinterDTO
        {
            Id = 999998, // Mismatched — route says 999999
            Name = "Mismatch Test",
            Make = "Test",
            Model = "Test",
            IsActive = true
        };

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/Printers/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(updateDto);

        var response = await _httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region DELETE Printer

    [Fact]
    public async Task DeletePrinter_Authenticated_ReturnsNoContent()
    {
        // Arrange - first create a printer to delete (one without prints)
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer To Delete",
            Make = "Delete Test",
            Model = "Delete Model",
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Act
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Printers/{createdPrinter.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var deleteResponse = await _httpClient.SendAsync(deleteRequest);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task DeletePrinter_Authenticated_PrinterNoLongerExists()
    {
        // Arrange - first create a printer to delete
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer To Delete And Verify",
            Make = "Delete Test",
            Model = "Delete Model",
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Act - delete the printer
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/Printers/{createdPrinter.Id}");
        deleteRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        await _httpClient.SendAsync(deleteRequest);

        // Assert - try to get the deleted printer
        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{createdPrinter.Id}");
        getRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var getResponse = await _httpClient.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeletePrinter_WithPrints_ReturnsBadRequest()
    {
        // Arrange - the seeded TestPrinterId has prints associated with it
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert - cannot delete printer that has prints
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrinter_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeletePrinter_NonExistent_ReturnsNotFound()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/Printers/999999");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region GET Loaded Filament Tests

    [Fact]
    public async Task GetLoadedFilament_Authenticated_ReturnsSuccess()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}/filament");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetLoadedFilament_Authenticated_ReturnsFilamentList()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}/filament");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);
        var filaments = (await response.Content.ReadFromJsonAsync<List<PrinterFilamentSummaryDto>>())!;

        // Assert
        Assert.NotNull(filaments);
        Assert.IsType<List<PrinterFilamentSummaryDto>>(filaments);
    }

    [Fact]
    public async Task GetLoadedFilament_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}/filament");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetLoadedFilament_NonExistentPrinter_ReturnsNotFound()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Printers/999999/filament");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region PUT Unload Printer Filament Tests

    [Fact]
    public async Task UnloadPrinterFilament_Authenticated_ReturnsSuccess()
    {
        // Arrange - first create a printer and load filament
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer For Unload Test",
            Make = "Test Make",
            Model = "Test Model",
            IsActive = true,
            LoadedFilaments = new List<AddPrinterFilamentDto>
            {
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId1 }
            }
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Act - unload all filaments
        var unloadRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}/filament/unload");
        unloadRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var unloadResponse = await _httpClient.SendAsync(unloadRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, unloadResponse.StatusCode);
    }

    [Fact]
    public async Task UnloadPrinterFilament_Authenticated_ClearsLoadedFilaments()
    {
        // Arrange - create a printer with loaded filament
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer For Unload Verify Test",
            Make = "Test Make",
            Model = "Test Model",
            IsActive = true,
            LoadedFilaments = new List<AddPrinterFilamentDto>
            {
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId1 },
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId2 }
            }
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Verify filament is loaded
        var getRequest1 = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{createdPrinter.Id}/filament");
        getRequest1.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var getResponse1 = await _httpClient.SendAsync(getRequest1);
        var filamentsBefore = (await getResponse1.Content.ReadFromJsonAsync<List<PrinterFilamentSummaryDto>>())!;
        Assert.NotEmpty(filamentsBefore);

        // Act - unload all filaments
        var unloadRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}/filament/unload");
        unloadRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        await _httpClient.SendAsync(unloadRequest);

        // Assert - verify filaments are now empty
        var getRequest2 = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{createdPrinter.Id}/filament");
        getRequest2.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var getResponse2 = await _httpClient.SendAsync(getRequest2);
        var filamentsAfter = (await getResponse2.Content.ReadFromJsonAsync<List<PrinterFilamentSummaryDto>>())!;
        Assert.Empty(filamentsAfter);
    }

    [Fact]
    public async Task UnloadPrinterFilament_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{IntegrationTestSeeder.TestPrinterId}/filament/unload");

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnloadPrinterFilament_NonExistentPrinter_ReturnsNotFound()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/Printers/999999/filament/unload");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region PUT Printer With Loaded Filament Tests

    [Fact]
    public async Task UpdatePrinter_WithLoadedFilament_ReturnsSuccess()
    {
        // Arrange - create a printer first
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer To Update With Filament",
            Make = "Test Make",
            Model = "Test Model",
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Arrange - update with loaded filament
        var updateDto = new AddPrinterDTO
        {
            Id = createdPrinter.Id,
            Name = createdPrinter.Name,
            Make = createdPrinter.Make,
            Model = createdPrinter.Model,
            IsActive = true,
            LoadedFilaments = new List<AddPrinterFilamentDto>
            {
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId1 },
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId2 }
            }
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        var updateResponse = await _httpClient.SendAsync(updateRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
    }

    [Fact]
    public async Task UpdatePrinter_WithLoadedFilament_SetsFilaments()
    {
        // Arrange - create a printer first
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer To Update With Filament Verify",
            Make = "Test Make",
            Model = "Test Model",
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Arrange - update with loaded filaments
        var updateDto = new AddPrinterDTO
        {
            Id = createdPrinter.Id,
            Name = createdPrinter.Name,
            Make = createdPrinter.Make,
            Model = createdPrinter.Model,
            IsActive = true,
            LoadedFilaments = new List<AddPrinterFilamentDto>
            {
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId1 },
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId2 },
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId3 }
            }
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        // Act
        await _httpClient.SendAsync(updateRequest);

        // Assert - verify filaments are set
        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{createdPrinter.Id}/filament");
        getRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var getResponse = await _httpClient.SendAsync(getRequest);
        var filaments = (await getResponse.Content.ReadFromJsonAsync<List<PrinterFilamentSummaryDto>>())!;

        Assert.NotNull(filaments);
        Assert.Equal(3, filaments.Count);
    }

    [Fact]
    public async Task UpdatePrinter_WithUnauthorizedFilament_ReturnsForbidden()
    {
        // Arrange - Create another user's filament that we don't have access to
        var otherUserFilamentId = Guid.NewGuid();

        // Create a printer first
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer For Filament Access Test",
            Make = "Test Make",
            Model = "Test Model",
            IsActive = true
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Act - try to update with a filament we don't have access to
        var updateDto = new AddPrinterDTO
        {
            Id = createdPrinter.Id,
            Name = createdPrinter.Name,
            Make = createdPrinter.Make,
            Model = createdPrinter.Model,
            IsActive = true,
            LoadedFilaments = new List<AddPrinterFilamentDto>
            {
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = otherUserFilamentId }
            }
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);

        var updateResponse = await _httpClient.SendAsync(updateRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
    }

    [Fact]
    public async Task UpdatePrinter_RemoveLoadedFilament_ClearsFilament()
    {
        // Arrange - create a printer with loaded filament
        var newPrinter = new AddPrinterDTO
        {
            Name = "Printer For Filament Removal Test",
            Make = "Test Make",
            Model = "Test Model",
            IsActive = true,
            LoadedFilaments = new List<AddPrinterFilamentDto>
            {
                new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = _filamentId1 }
            }
        };

        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        createRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        createRequest.Content = JsonContent.Create(newPrinter);
        var createResponse = await _httpClient.SendAsync(createRequest);
        var createdPrinter = (await createResponse.Content.ReadFromJsonAsync<PrinterDetailDto>())!;

        // Verify filament is loaded
        var getRequest1 = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{createdPrinter.Id}/filament");
        getRequest1.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var getResponse1 = await _httpClient.SendAsync(getRequest1);
        var filamentsBefore = (await getResponse1.Content.ReadFromJsonAsync<List<PrinterFilamentSummaryDto>>())!;
        Assert.NotEmpty(filamentsBefore);

        // Act - update with empty filaments list
        var updateDto = new AddPrinterDTO
        {
            Id = createdPrinter.Id,
            Name = createdPrinter.Name,
            Make = createdPrinter.Make,
            Model = createdPrinter.Model,
            IsActive = true,
            LoadedFilaments = new List<AddPrinterFilamentDto>()
        };

        var updateRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{createdPrinter.Id}");
        updateRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        updateRequest.Content = JsonContent.Create(updateDto);
        await _httpClient.SendAsync(updateRequest);

        // Assert - verify filaments are now empty
        var getRequest2 = new HttpRequestMessage(HttpMethod.Get, $"/api/Printers/{createdPrinter.Id}/filament");
        getRequest2.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var getResponse2 = await _httpClient.SendAsync(getRequest2);
        var filamentsAfter = (await getResponse2.Content.ReadFromJsonAsync<List<PrinterFilamentSummaryDto>>())!;
        Assert.Empty(filamentsAfter);
    }

    #endregion
}
