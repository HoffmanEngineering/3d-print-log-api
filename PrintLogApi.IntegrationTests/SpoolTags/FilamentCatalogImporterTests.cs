using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.SpoolTags.Catalog;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

// The importer keeps its last ETag in a static field, so these classes must not overlap.
[Collection("SpoolTagCatalogImport")]
public class FilamentCatalogImporterTests
{
    private const string BlackPetgId = "polymaker_petg_polylitepetgblack_1000_175_c";
    private const string BlueId = "polymaker_pla_panchroma(formerlypolylite)luminousblue_1000_175_n";
    private const string GreenBrownId = "anycubic_pla_mattegreen-brown_1000_175_c";
    private const string NoSpoolWeightId = "azurefilm_petg_hyperspeedarmygreen_1000_175_n";

    private static JsonArray Sample() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "spool-tags", "spoolmandb-sample.json")))!.AsArray();

    private static string Json(JsonArray array) => array.ToJsonString();

    private static async Task<CatalogImportResult> Import(CatalogTestHost host)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FilamentCatalogImporter>().ImportAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<PrintLogApi.Models.FilamentCatalogEntry>> Rows(CatalogTestHost host)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>()
            .FilamentCatalogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FirstImport_AddsEveryEntry_WithItsFields()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()));

        var result = await Import(host);

        Assert.Equal(new CatalogImportResult(6, 0, 0, false), result);
        var rows = await Rows(host);
        Assert.Equal(6, rows.Count);
        var blue = rows.Single(r => r.Id == BlueId);
        Assert.Equal("Polymaker", blue.Manufacturer);
        Assert.Equal("PLA", blue.Material);
        Assert.Equal(1.75, blue.DiameterMm);
        Assert.Equal(1000, blue.WeightG);
        Assert.Equal(140, blue.SpoolWeightG);
        Assert.Equal("419ec2", blue.ColorHex);
        Assert.Equal(190, blue.ExtruderTempMin);
        Assert.Equal(230, blue.ExtruderTempMax);
        Assert.Null(blue.ExtruderTemp);
        Assert.Null(blue.RemovedAt);
    }

    [Fact]
    public async Task ColorHexes_StoreJson_AndTheFirstHexAsColorHex()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()));

        await Import(host);

        var entry = (await Rows(host)).Single(r => r.Id == GreenBrownId);
        Assert.Equal("744E10", entry.ColorHex);
        Assert.Equal(["744E10", "009639"], JsonSerializer.Deserialize<string[]>(entry.ColorHexesJson!)!);
    }

    [Fact]
    public async Task NullSpoolWeight_StaysNull()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()));

        await Import(host);

        Assert.Null((await Rows(host)).Single(r => r.Id == NoSpoolWeightId).SpoolWeightG);
    }

    [Fact]
    public async Task SecondImport_SendsTheETag_AndTreats304AsNotModified()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()), "\"abc\"");
        await Import(host);

        var second = await Import(host);

        Assert.Equal(new CatalogImportResult(0, 0, 0, true), second);
        Assert.Equal([null, "\"abc\""], host.Stub.IfNoneMatch.ToArray());
        Assert.Equal(6, (await Rows(host)).Count);
    }

    [Fact]
    public async Task RemovedAndChangedEntries_AreSoftDeletedAndUpdated()
    {
        await using var host = new CatalogTestHost();
        var sample = Sample();
        host.Stub.Serve(Json(sample), null);
        await Import(host);

        var next = Sample();
        var removed = next.Single(n => n!["id"]!.GetValue<string>() == BlackPetgId)!;
        next.Remove(removed);
        next.Single(n => n!["id"]!.GetValue<string>() == BlueId)!["name"] = "Renamed Blue";
        host.Stub.Serve(Json(next), null);

        var result = await Import(host);

        Assert.Equal(new CatalogImportResult(0, 1, 1, false), result);
        var rows = await Rows(host);
        Assert.Equal(6, rows.Count);
        Assert.NotNull(rows.Single(r => r.Id == BlackPetgId).RemovedAt);
        Assert.Equal("Renamed Blue", rows.Single(r => r.Id == BlueId).Name);
        Assert.Null(rows.Single(r => r.Id == BlueId).RemovedAt);
    }

    [Fact]
    public async Task AnEntryThatComesBack_ClearsRemovedAt()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()), null);
        await Import(host);
        var without = Sample();
        without.Remove(without.Single(n => n!["id"]!.GetValue<string>() == BlackPetgId));
        host.Stub.Serve(Json(without), null);
        await Import(host);
        host.Stub.Serve(Json(Sample()), null);

        var result = await Import(host);

        Assert.Equal(new CatalogImportResult(0, 1, 0, false), result);
        Assert.Null((await Rows(host)).Single(r => r.Id == BlackPetgId).RemovedAt);
    }

    [Fact]
    public async Task NoChange_ReportsNothing()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()), null);
        await Import(host);

        Assert.Equal(new CatalogImportResult(0, 0, 0, false), await Import(host));
    }

    [Fact]
    public async Task ServerError_Throws_AndLeavesTheTableAlone()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()), null);
        await Import(host);
        host.Stub.ServeStatus(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<CatalogFetchException>(() => Import(host));

        var rows = await Rows(host);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Null(r.RemovedAt));
    }

    [Fact]
    public async Task InvalidJson_Throws_AndLeavesTheTableAlone()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve("{ this is not json", null);

        await Assert.ThrowsAsync<CatalogFetchException>(() => Import(host));

        Assert.Empty(await Rows(host));
    }

    [Fact]
    public async Task OversizedBody_Throws_AndLeavesTheTableAlone()
    {
        await using var host = new CatalogTestHost(maxCatalogBytes: 1024);
        host.Stub.Serve(Json(Sample()), null);

        await Assert.ThrowsAsync<CatalogFetchException>(() => Import(host));

        Assert.Empty(await Rows(host));
    }

    [Fact]
    public async Task AnEmptyFeed_IsRefused_SoItCannotRemoveTheWholeCatalog()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(Json(Sample()), null);
        await Import(host);
        host.Stub.Serve("[]", null);

        await Assert.ThrowsAsync<CatalogFetchException>(() => Import(host));

        Assert.All(await Rows(host), r => Assert.Null(r.RemovedAt));
    }
}
