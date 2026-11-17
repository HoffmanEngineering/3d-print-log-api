using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A loaded-spool row naming another user's spool: left out of every read on both sides, and
/// never charged by a print event.
/// </summary>
public class ForeignSpoolLoadTests(CustomWebApplicationFactory factory)
    : ForeignSpoolTestBase(factory), IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task ExistingForeignLoad_IsLeftOutOfEveryPrinterRead()
    {
        var (victim, other) = await SeedPairAsync();
        await SeedLoadAsync(other.Printer, victim.Filament.Id);

        var detail = await GetJsonAsync($"/api/Printers/{other.Printer.Id}", other.User);
        var loaded = await GetJsonAsync($"/api/Printers/{other.Printer.Id}/filament", other.User);
        var summary = await GetJsonAsync("/api/Printers/summary?pageSize=250&includeInactive=true", other.User);
        var item = summary.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == other.Printer.Id);

        foreach (var rows in new[] { detail.GetProperty("loadedFilaments"), loaded, item.GetProperty("loadedFilaments") })
        {
            var ids = rows.EnumerateArray().Select(r => r.GetProperty("filament").GetProperty("id").GetGuid()).ToList();
            Assert.Equal([other.Filament.Id], ids);
        }
        Assert.DoesNotContain(victim.Filament.Notes!, detail.ToString());
        Assert.DoesNotContain(victim.Filament.Notes!, loaded.ToString());
        // The printer's own spool still reports its remaining figure.
        Assert.Equal(InitialMg, detail.GetProperty("loadedFilaments")[0].GetProperty("filament").GetProperty("filamentRemaining").GetInt64());
    }

    [Fact]
    public async Task ExistingForeignLoad_IsLeftOutOfThePutResponse()
    {
        var (victim, other) = await SeedPairAsync();
        await SeedLoadAsync(other.Printer, victim.Filament.Id);

        var (status, body) = await SendAsync(As(PrinterBody(HttpMethod.Put, $"/api/Printers/{other.Printer.Id}", other.Filament.Id, other.Printer.Id), other.User));

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.DoesNotContain(victim.Filament.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(victim.Filament.Notes!, body);
    }

    [Fact]
    public async Task ExistingForeignLoad_BridgePutRebuiltFromGet_SucceedsAndUnloadsIt()
    {
        var (victim, other) = await SeedPairAsync();
        await SeedLoadAsync(other.Printer, victim.Filament.Id);

        // What printlog-bridge's slot growth sends: the GET body as read, less what PUT takes in
        // another shape, with the category's nickname and the loaded spools' ids.
        var detail = JsonNode.Parse((await GetJsonAsync($"/api/Printers/{other.Printer.Id}", other.User)).GetRawText())!.AsObject();
        var body = new JsonObject();
        foreach (var (key, value) in detail)
        {
            if (key is not ("images" or "loadedFilaments" or "category" or "slotCount"))
            {
                body[key] = value?.DeepClone();
            }
        }
        body["id"] = other.Printer.Id;
        body["slotCount"] = 2;
        if (detail["category"]?["nickname"] is { } nickname)
        {
            body["category"] = nickname.DeepClone();
        }
        body["loadedFilaments"] = new JsonArray(detail["loadedFilaments"]!.AsArray()
            .Where(row => row?["filament"]?["id"] is not null)
            .Select(row => (JsonNode)new JsonObject { ["id"] = row!["id"]?.DeepClone(), ["filamentId"] = row["filament"]!["id"]!.DeepClone() })
            .ToArray());

        var (status, text) = await SendAsync(As(new HttpRequestMessage(HttpMethod.Put, $"/api/Printers/{other.Printer.Id}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        }, other.User));

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {text}");
        var foreign = Assert.Single(await LoadedRowsForFilamentAsync(victim.Filament.Id), r => r.PrinterId == other.Printer.Id);
        Assert.NotNull(foreign.UnloadedDateTime);
        var own = Assert.Single(await LoadedRowsForFilamentAsync(other.Filament.Id));
        Assert.Null(own.UnloadedDateTime);
        // The victim's own load is untouched.
        Assert.Null(Assert.Single(await LoadedRowsForFilamentAsync(victim.Filament.Id), r => r.PrinterId == victim.Printer.Id).UnloadedDateTime);
    }

    [Fact]
    public async Task ExistingForeignLoad_DoesNotShowAsLoadedInTheOwnersSpoolList()
    {
        var victim = await SeedOwnerAsync(loadOwnSpool: false);
        var other = await SeedOwnerAsync();
        await SeedLoadAsync(other.Printer, victim.Filament.Id);

        var list = await GetJsonAsync("/api/Filaments?pageSize=100", victim.User);
        var mine = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == victim.Filament.Id);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("loadedInPrinter").ValueKind);

        var loadedOnly = await GetJsonAsync("/api/Filaments?pageSize=100&showLoadedFilamentOnly=true", victim.User);
        Assert.DoesNotContain(loadedOnly.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == victim.Filament.Id);
    }

    [Fact]
    public async Task ExistingForeignLoad_OwnersOwnLoadIsTheOneShown()
    {
        var (victim, other) = await SeedPairAsync();
        await SeedLoadAsync(other.Printer, victim.Filament.Id);

        var list = await GetJsonAsync("/api/Filaments?pageSize=100&showLoadedFilamentOnly=true", victim.User);
        var mine = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == victim.Filament.Id);
        Assert.Equal(victim.Printer.Id, mine.GetProperty("loadedInPrinter").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task ExistingForeignLoad_NotifierStartAndFinish_RecordUsageWithNoSpool()
    {
        var (victim, other) = await SeedPairAsync(otherLoadsOwnSpool: false);
        await SeedLoadAsync(other.Printer, victim.Filament.Id);
        var run = $"run{Guid.NewGuid():N}";

        await NotifierAsync(other.User, "moonraker-notifier-started.json", other.Printer.Id, run);
        Assert.Null(Assert.Single((await PrintForRunAsync(run)).FilamentUsage!).FilamentId);

        await NotifierAsync(other.User, "moonraker-notifier-complete.json", other.Printer.Id, run, filamentUsedMm: 330_000);
        var row = Assert.Single((await PrintForRunAsync(run)).FilamentUsage!);
        Assert.Null(row.FilamentId);
        Assert.Equal(330, row.LengthInM);
        Assert.Equal(InitialMg - OwnPrintMg, await RemainingAsync(victim));
    }

    [Fact]
    public async Task ExistingForeignLoad_NotifierStillLinksTheOwnersOwnSpool()
    {
        var owner = await SeedOwnerAsync();
        var run = $"run{Guid.NewGuid():N}";

        await NotifierAsync(owner.User, "moonraker-notifier-started.json", owner.Printer.Id, run);
        await NotifierAsync(owner.User, "moonraker-notifier-complete.json", owner.Printer.Id, run);

        Assert.Equal(owner.Filament.Id, Assert.Single((await PrintForRunAsync(run)).FilamentUsage!).FilamentId);
        Assert.True(await RemainingAsync(owner) < InitialMg);
    }

    [Fact]
    public async Task ExistingForeignLoad_OctoPrintStart_RecordsUsageWithNoSpool()
    {
        var (victim, other) = await SeedPairAsync(otherLoadsOwnSpool: false);
        await SeedLoadAsync(other.Printer, victim.Filament.Id);
        var run = $"run{Guid.NewGuid():N}";
        var hash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + "00000000";
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(Fixture("octoprint-print-started.json"))!;
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value.Replace("__PRINTER_ID__", other.Printer.Id.ToString()).Replace("__RUN__", run).Replace("__HASH__", hash)), name);
        }

        var (status, text) = await SendAsync(As(new HttpRequestMessage(HttpMethod.Post, "/api/Octoprint") { Content = content }, other.User));

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {text}");
        Assert.Null(Assert.Single((await PrintForRunAsync(run)).FilamentUsage!).FilamentId);
    }
}
