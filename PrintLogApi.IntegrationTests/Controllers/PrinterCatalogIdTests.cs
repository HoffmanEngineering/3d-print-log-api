using System.Net;
using System.Text.Json;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A printer can point at a catalog model (spec 6.1, 6.6): the id is validated on create and update,
/// a retired model is allowed only where the printer already had it, and create stamps CreatedAt.
/// </summary>
public class PrinterCatalogIdTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public PrinterCatalogIdTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task Post_UnknownCatalogModelId_Returns400WithCode()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Printers", NewPrinter("no-such-model-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("printer_model_not_found", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_HiddenCatalogModelId_Returns400WithCode()
    {
        var id = await SeedModelAsync(hidden: true);

        var response = await SendAsync(HttpMethod.Post, "/api/Printers", NewPrinter(id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("printer_model_not_found", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_RetiredCatalogModelId_Returns400WithCode()
    {
        var id = await SeedModelAsync(retired: true);

        var response = await SendAsync(HttpMethod.Post, "/api/Printers", NewPrinter(id));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("printer_model_not_found", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Post_KnownCatalogModelId_Returns201AndEchoesIt()
    {
        var id = await SeedModelAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/Printers", NewPrinter(id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(id, created.CatalogModelId);

        var summary = await GetSummaryAsync(created.Id!.Value);
        Assert.Equal(id, summary.CatalogModelId);
    }

    [Fact]
    public async Task Post_WithoutCatalogModelId_StillWorks()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Printers", NewPrinter(null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!;
        Assert.Null(created.CatalogModelId);
    }

    [Fact]
    public async Task Post_StampsCreatedAt()
    {
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);

        var response = await SendAsync(HttpMethod.Post, "/api/Printers", NewPrinter(null));
        var created = (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = await db.Printers.FindAsync([created.Id!.Value], TestContext.Current.CancellationToken);
        Assert.NotNull(printer!.CreatedAt);
        Assert.InRange(printer.CreatedAt!.Value, before, DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Put_KeepingNowRetiredId_Succeeds()
    {
        var id = await SeedModelAsync();
        var created = await CreateAsync(id);
        await RetireAsync(id);

        var response = await SendAsync(HttpMethod.Put, $"/api/Printers/{created.Id}", PutBody(created, id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(id, updated.CatalogModelId);
    }

    [Fact]
    public async Task Put_SwitchingToADifferentRetiredId_Returns400()
    {
        var original = await SeedModelAsync();
        var retired = await SeedModelAsync(retired: true);
        var created = await CreateAsync(original);

        var response = await SendAsync(HttpMethod.Put, $"/api/Printers/{created.Id}", PutBody(created, retired));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("printer_model_not_found", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Put_UnknownId_Returns400()
    {
        var created = await CreateAsync(null);

        var response = await SendAsync(HttpMethod.Put, $"/api/Printers/{created.Id}", PutBody(created, "nope-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("printer_model_not_found", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Put_SwitchingToAnotherLiveModel_Succeeds()
    {
        var first = await SeedModelAsync();
        var second = await SeedModelAsync();
        var created = await CreateAsync(first);

        var response = await SendAsync(HttpMethod.Put, $"/api/Printers/{created.Id}", PutBody(created, second));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(second, updated.CatalogModelId);
    }

    private static AddPrinterDTO NewPrinter(string? catalogModelId) => new()
    {
        Name = $"Catalog printer {Guid.NewGuid():N}",
        Make = "Bambu Lab",
        Model = "P1S",
        IsActive = true,
        CatalogModelId = catalogModelId,
    };

    private static AddPrinterDTO PutBody(PrinterDetailDto current, string? catalogModelId) => new()
    {
        Id = current.Id,
        Name = current.Name,
        Make = current.Make,
        Model = current.Model,
        IsActive = true,
        CatalogModelId = catalogModelId,
    };

    private async Task<PrinterDetailDto> CreateAsync(string? catalogModelId)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Printers", NewPrinter(catalogModelId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<string> SeedModelAsync(bool hidden = false, bool retired = false)
    {
        var id = $"test-model-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.PrinterModels.Add(new PrinterModel
        {
            Id = id,
            Make = "Bambu Lab",
            Model = "P1S",
            AliasesJson = "[]",
            Technology = "fdm",
            NozzleOptionsJson = "[]",
            SourcesJson = "[]",
            Hidden = hidden,
            Retired = retired,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task RetireAsync(string id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var model = await db.PrinterModels.FindAsync([id], TestContext.Current.CancellationToken);
        model!.Retired = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<PrinterSummarySimpleDto> GetSummaryAsync(long printerId)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/Printers/summary?includeInactive=true&pageSize=100");
        Assert.True(response.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.Deserialize<PrinterSummarySimpleDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        return items.Single(i => i.Id == printerId);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
