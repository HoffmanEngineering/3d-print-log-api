using System.Text;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements;
using PrintLogApi.Models.DTOs.CuraSettings;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class SlicerNamesTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SlicerNamesTests(CustomWebApplicationFactory factory) => _factory = factory;

    // Wire values are the uploader parsers' SlicerName strings (Slic3rPostProcessingUploader).
    [Theory]
    [InlineData(null, "cura")]
    [InlineData("", "cura")]
    [InlineData("PrusaSlicer", "prusaslicer")]
    [InlineData("OrcaSlicer", "orcaslicer")]
    [InlineData("BambuStudioSlicer", "bambustudio")]
    [InlineData("AnycubicSlicerNext", "anycubic")]
    [InlineData("FLSunSlicer", "flsun")]
    [InlineData("orcaslicer", "orcaslicer")]
    [InlineData("Snapmaker Orca", "other")]
    [InlineData("ElegooSlicer", "other")]
    public void Normalize_MapsUploaderWireValues(string? wire, string expected)
    {
        Assert.Equal(expected, SlicerNames.Normalize(wire));
    }

    [Fact]
    public void BadgeSlicers_AreTheFiveWithDedicatedBadges()
    {
        Assert.Equal(
            new[] { "anycubic", "bambustudio", "cura", "orcaslicer", "prusaslicer" },
            SlicerNames.BadgeSlicers.Order());
    }

    [Fact]
    public async Task SaveSettings_PersistsSlicer()
    {
        var client = _factory.CreateClient();
        var body = """{"slicer":"OrcaSlicer","curaVersion":"2.3.1","pluginVersion":"1.2.0","settings":{}}""";

        var response = await client.PostAsync("/api/Cura/settings",
            new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<NewCuraSettingsDto>(TestContext.Current.CancellationToken))!.NewSettingId;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await db.CuraSettings.AsNoTracking().SingleAsync(s => s.Id == id, TestContext.Current.CancellationToken);

        // Stored raw; normalization happens when a print is created from it.
        Assert.Equal("OrcaSlicer", stored.Slicer);
    }
}
