using System.Text.Json;
using ModelContextProtocol.Client;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>End-to-end tests for adjust_material_remaining, including the capacity bounds.</summary>
public class AdjustMaterialToolTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;

    public AdjustMaterialToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

    // Creates a 1000 g weight-measured material and returns its id, so each test mutates its own
    // material rather than a shared read-fixture.
    private async Task<Guid> CreateMaterial(McpClient client, string name)
    {
        await client.CallToolAsync("create_material", new Dictionary<string, object?>
        {
            ["displayName"] = name,
            ["materialType"] = "PLA",
            ["materialCategoryNickname"] = "filament",
            ["densityGramPerCubicCm"] = 1.24,
            ["diameterMm"] = 1.75,
            ["source"] = "Weight",
            ["initialAmount"] = 1000.0, // 1000 g
        });

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return ctx.Filaments.Where(f => f.DisplayName == name).Select(f => f.Id).First();
    }

    [Fact]
    public async Task Adjust_Down_ReturnsBeforeAndAfter()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust Down Mat");

        var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object?>
        {
            ["materialId"] = id,
            ["source"] = "Weight",
            ["delta"] = -200.0,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError != true);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
        // Asserted on the parsed fields, not a substring of the raw JSON: "800" would match a
        // material id or any other number that happened to contain it.
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(1000.0, doc.RootElement.GetProperty("beforeGrams").GetDouble());
        Assert.Equal(800.0, doc.RootElement.GetProperty("afterGrams").GetDouble());
    }

    // The reported values are ALWAYS grams, whatever unit the delta was expressed in — which is
    // why the fields are not named *InSourceUnit. 5000 mm of 1.75 mm PLA at 1.24 g/cm3 is ~14.9 g,
    // so a Length delta still reads back on the weight scale.
    [Fact]
    public async Task Adjust_ByLength_StillReportsGrams()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust Length Mat");

        var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object?>
        {
            ["materialId"] = id,
            ["source"] = "Length",
            ["delta"] = -5000.0, // mm
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError != true);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(1000.0, doc.RootElement.GetProperty("beforeGrams").GetDouble());
        // Grams, not the 5000 mm that was passed in.
        Assert.InRange(doc.RootElement.GetProperty("afterGrams").GetDouble(), 984.0, 986.0);
        // The old contract carried a hardcoded "g" SourceUnit that named nothing the caller chose.
        Assert.False(doc.RootElement.TryGetProperty("sourceUnit", out _));
    }

    [Fact]
    public async Task Adjust_BelowZero_IsRejected()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust BelowZero Mat");

        var isError = await McpDataWebApplicationFactory.IsToolError(client, "adjust_material_remaining",
            new Dictionary<string, object?> { ["materialId"] = id, ["source"] = "Weight", ["delta"] = -2000.0 });

        Assert.True(isError);
    }

    [Fact]
    public async Task Adjust_AboveCapacity_IsRejected()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust AboveCap Mat");

        var isError = await McpDataWebApplicationFactory.IsToolError(client, "adjust_material_remaining",
            new Dictionary<string, object?> { ["materialId"] = id, ["source"] = "Weight", ["delta"] = 500.0 });

        Assert.True(isError);
    }

    // create_material refuses a diameter-tracking material without a diameter, or a zero density,
    // but the web app has saved both, so the tests below set them on the row directly.
    private async Task SetConversionInputs(Guid id, double density, double? diameterMm)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var material = ctx.Filaments.Single(f => f.Id == id);
        material.MaterialDensityGramPerCubicCm = density;
        material.DiameterMm = diameterMm;
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> AdjustmentCount(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return ctx.FilamentAdjustments.Count(a => a.FilamentId == id);
    }

    private static async Task AssertUnconvertible(McpClient client, Guid id, string source, double delta)
    {
        var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object?>
        {
            ["materialId"] = id,
            ["source"] = source,
            ["delta"] = delta,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
        Assert.Contains("missing the density/diameter needed to convert the adjustment to a weight", text);
    }

    [Fact]
    public async Task Adjust_ByLength_WithoutDiameter_IsRejected()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust Length No Diameter Mat");
        await SetConversionInputs(id, density: 1.24, diameterMm: null);

        await AssertUnconvertible(client, id, "Length", -5000.0);
        Assert.Equal(0, await AdjustmentCount(id));
    }

    // Density 0 turns any length into 0 g: the adjustment would be saved and count for nothing.
    [Fact]
    public async Task Adjust_ByLength_WithZeroDensity_IsRejected()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust Length Zero Density Mat");
        await SetConversionInputs(id, density: 0, diameterMm: 1.75);

        await AssertUnconvertible(client, id, "Length", -5000.0);
        Assert.Equal(0, await AdjustmentCount(id));
    }

    // Volume to weight needs only the density, so a missing diameter must not block it.
    [Fact]
    public async Task Adjust_ByVolume_WithoutDiameter_ConvertsFromDensity()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust Volume No Diameter Mat");
        await SetConversionInputs(id, density: 1.24, diameterMm: null);

        var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object?>
        {
            ["materialId"] = id,
            ["source"] = "Volume",
            ["delta"] = -100.0, // ml, 124 g at 1.24 g/cm3
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError != true);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(876.0, doc.RootElement.GetProperty("afterGrams").GetDouble());
    }

    // A weight needs no density, but deriving its volume from density 0 would divide by zero and
    // store Infinity, which SQL Server's float column rejects.
    [Fact]
    public async Task Adjust_ByWeight_WithZeroDensity_SavesWithoutDerivedVolume()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var id = await CreateMaterial(client, "Adjust Weight Zero Density Mat");
        await SetConversionInputs(id, density: 0, diameterMm: null);

        var result = await client.CallToolAsync("adjust_material_remaining", new Dictionary<string, object?>
        {
            ["materialId"] = id,
            ["source"] = "Weight",
            ["delta"] = -200.0,
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError != true);
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = ctx.FilamentAdjustments.Single(a => a.FilamentId == id);
        Assert.Equal(-200_000, row.AmountMg);
        Assert.Null(row.VolumeMl);
        Assert.Null(row.LengthInM);
    }

    [Fact]
    public async Task Adjust_ForeignMaterial_ReturnsNotFound()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "adjust_material_remaining",
            new Dictionary<string, object?>
            {
                ["materialId"] = Guid.NewGuid(),
                ["source"] = "Weight",
                ["delta"] = -1.0,
            });

        Assert.Equal("not_found", code);
    }
}
