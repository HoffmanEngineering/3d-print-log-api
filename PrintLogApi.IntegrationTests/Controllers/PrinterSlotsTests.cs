using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Per-slot loaded filament (#146): a multi-tool printer knows which spool is in which position,
/// a spool is loaded in one place at a time, and single-tool printers behave as before.
/// </summary>
public class PrinterSlotsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public PrinterSlotsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    #region Loading and unloading slots

    [Fact]
    public async Task LoadSlot_PutsTheSpoolInThatSlot()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();

        var response = await LoadSlotAsync(printerId, 2, spool, slotLabel: "T2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var loaded = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Equal(spool, loaded.Filament!.Id);
        Assert.Equal(2, loaded.Slot);
        Assert.Equal("T2", loaded.SlotLabel);
        Assert.NotEqual(default, loaded.LoadedAt);
    }

    [Fact]
    public async Task GetLoadedFilament_ReturnsSlotsInOrder()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (t3, t0, t1) = (await CreateFilamentAsync(), await CreateFilamentAsync(), await CreateFilamentAsync());

        await LoadSlotAsync(printerId, 3, t3);
        await LoadSlotAsync(printerId, 0, t0);
        await LoadSlotAsync(printerId, 1, t1);

        var loaded = await GetLoadedAsync(printerId);
        Assert.Equal([0, 1, 3], loaded.Select(l => l.Slot));
        Assert.Equal([t0, t1, t3], loaded.Select(l => l.Filament!.Id));
    }

    [Fact]
    public async Task GetPrinter_ReturnsSlotCountAndSlotsInOrder()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (t2, t0) = (await CreateFilamentAsync(), await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 2, t2);
        await LoadSlotAsync(printerId, 0, t0);

        var printer = await GetPrinterAsync(printerId);

        Assert.Equal(4, printer.SlotCount);
        Assert.Equal([0, 2], printer.LoadedFilaments!.Select(l => l.Slot));
    }

    [Fact]
    public async Task LoadSlot_SpoolInAnotherSlot_MovesIt()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        await LoadSlotAsync(printerId, 0, spool);

        await LoadSlotAsync(printerId, 3, spool);

        var loaded = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Equal(3, loaded.Slot);
    }

    [Fact]
    public async Task LoadSlot_SpoolOnAnotherPrinter_MovesIt()
    {
        var first = await CreatePrinterAsync(slotCount: 4);
        var second = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        await LoadSlotAsync(first, 1, spool);

        await LoadSlotAsync(second, 2, spool);

        Assert.Empty(await GetLoadedAsync(first));
        Assert.Equal(2, Assert.Single(await GetLoadedAsync(second)).Slot);
    }

    [Fact]
    public async Task LoadSlot_SpoolLoadedWithoutASlot_MovesIt()
    {
        // Loaded through the printer PUT, which has no slots.
        var first = await CreatePrinterAsync(slotCount: 1);
        var spool = await CreateFilamentAsync();
        await PutPrinterAsync(first, loaded: [spool]);
        var second = await CreatePrinterAsync(slotCount: 4);

        await LoadSlotAsync(second, 0, spool);

        Assert.Empty(await GetLoadedAsync(first));
        Assert.Single(await GetLoadedAsync(second));
    }

    [Fact]
    public async Task LoadSlot_OccupiedSlot_ReplacesTheSpoolThere()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (before, after) = (await CreateFilamentAsync(), await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 1, before);

        await LoadSlotAsync(printerId, 1, after);

        var loaded = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Equal(after, loaded.Filament!.Id);
    }

    [Fact]
    public async Task LoadSlot_KeepsTheUnloadedRowAsHistory()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (before, after) = (await CreateFilamentAsync(), await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 1, before);

        await LoadSlotAsync(printerId, 1, after);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var history = await db.PrinterFilament.IgnoreQueryFilters()
            .SingleAsync(pf => pf.PrinterId == printerId && pf.FilamentId == before, TestContext.Current.CancellationToken);
        Assert.NotNull(history.UnloadedDateTime);
        Assert.Equal(1, history.Slot);
    }

    [Fact]
    public async Task LoadSlot_TheSameSpoolAgain_KeepsItsLoadedTime()
    {
        // The bridge reasserts slots on every reconnect; that is not a reload.
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var spool = await CreateFilamentAsync();
        await LoadSlotAsync(printerId, 1, spool);
        var first = Assert.Single(await GetLoadedAsync(printerId));

        await LoadSlotAsync(printerId, 1, spool, slotLabel: "Left");

        var again = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.LoadedAt, again.LoadedAt);
        Assert.Equal("Left", again.SlotLabel);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task LoadSlot_OutsideTheSlotCount_IsBadRequest(int slot)
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);

        var response = await LoadSlotAsync(printerId, slot, await CreateFilamentAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await GetLoadedAsync(printerId));
    }

    [Fact]
    public async Task LoadSlot_WithoutAFilament_IsBadRequest()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);

        var response = await SendAsync(HttpMethod.Put, $"/api/Printers/{printerId}/slots/0", new LoadPrinterSlotDto());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoadSlot_ALabelOver20Characters_IsBadRequest()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);

        var response = await LoadSlotAsync(printerId, 0, await CreateFilamentAsync(), slotLabel: new string('x', 21));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoadSlot_AnotherUsersPrinter_IsForbidden()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4, userOAuthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await LoadSlotAsync(printerId, 0, await CreateFilamentAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LoadSlot_AnotherUsersFilament_IsForbiddenAndLoadsNothing()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var theirs = await CreateFilamentAsync(IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await LoadSlotAsync(printerId, 0, theirs);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await GetLoadedAsync(printerId));
    }

    [Fact]
    public async Task LoadSlot_MissingPrinter_IsNotFound()
    {
        var response = await LoadSlotAsync(999_999, 0, await CreateFilamentAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnloadSlot_RemovesOnlyThatSlot()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (t0, t1) = (await CreateFilamentAsync(), await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 0, t0);
        await LoadSlotAsync(printerId, 1, t1);

        var response = await SendAsync(HttpMethod.Delete, $"/api/Printers/{printerId}/slots/0");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var loaded = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Equal(t1, loaded.Filament!.Id);
    }

    [Fact]
    public async Task UnloadSlot_AnEmptySlot_IsNoContent()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);

        var response = await SendAsync(HttpMethod.Delete, $"/api/Printers/{printerId}/slots/2");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task UnloadSlot_AnotherUsersPrinter_IsForbidden()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4, userOAuthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await SendAsync(HttpMethod.Delete, $"/api/Printers/{printerId}/slots/0");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnloadAll_ClearsEverySlot()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        await LoadSlotAsync(printerId, 0, await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 3, await CreateFilamentAsync());

        await SendAsync(HttpMethod.Put, $"/api/Printers/{printerId}/filament/unload");

        Assert.Empty(await GetLoadedAsync(printerId));
    }

    #endregion

    #region Single-tool printers and the printer PUT

    [Fact]
    public async Task NewPrinter_HasOneSlotByDefault()
    {
        var printerId = await CreatePrinterAsync(slotCount: null);

        Assert.Equal(1, (await GetPrinterAsync(printerId)).SlotCount);
    }

    [Fact]
    public async Task PutPrinter_WithoutASlotCount_KeepsTheOneItHas()
    {
        // The web and Android edit forms predate slots and never send one.
        var printerId = await CreatePrinterAsync(slotCount: 4);

        await PutPrinterAsync(printerId, loaded: [], slotCount: null);

        Assert.Equal(4, (await GetPrinterAsync(printerId)).SlotCount);
    }

    [Fact]
    public async Task PutPrinter_SetsTheSlotCount()
    {
        var printerId = await CreatePrinterAsync(slotCount: null);

        await PutPrinterAsync(printerId, loaded: [], slotCount: 4);

        Assert.Equal(4, (await GetPrinterAsync(printerId)).SlotCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task PutPrinter_ASlotCountOutOfRange_IsBadRequest(int slotCount)
    {
        var printerId = await CreatePrinterAsync(slotCount: null);

        var response = await PutPrinterAsync(printerId, loaded: [], slotCount: slotCount);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutPrinter_TheSameLoadedSpools_KeepsTheirSlots()
    {
        // The edit form sends the loaded list back unchanged when the user edits the name.
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (t0, t2) = (await CreateFilamentAsync(), await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 0, t0);
        await LoadSlotAsync(printerId, 2, t2, slotLabel: "T2");

        var response = await PutPrinterAsync(printerId, loaded: [t0, t2]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var loaded = await GetLoadedAsync(printerId);
        Assert.Equal([0, 2], loaded.Select(l => l.Slot));
        Assert.Equal("T2", loaded[1].SlotLabel);
    }

    [Fact]
    public async Task PutPrinter_ASpoolAddedThere_HasNoSlot()
    {
        var printerId = await CreatePrinterAsync(slotCount: 1);
        var spool = await CreateFilamentAsync();

        await PutPrinterAsync(printerId, loaded: [spool]);

        var loaded = Assert.Single(await GetLoadedAsync(printerId));
        Assert.Null(loaded.Slot);
    }

    [Fact]
    public async Task AddPrint_OnASingleToolPrinter_StillLoadsItsSpool()
    {
        var printerId = await CreatePrinterAsync(slotCount: 1);
        var spool = await CreateFilamentAsync();

        await CreatePrintAsync(printerId, [UsageRow(spool)]);

        Assert.Equal(spool, Assert.Single(await GetLoadedAsync(printerId)).Filament!.Id);
    }

    [Fact]
    public async Task AddPrint_OnAMultiSlotPrinter_LeavesTheSlotsAlone()
    {
        // Replacing the loaded list with the print's spools would unload every other tool.
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (t0, t1, other) = (await CreateFilamentAsync(), await CreateFilamentAsync(), await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 0, t0);
        await LoadSlotAsync(printerId, 1, t1);

        await CreatePrintAsync(printerId, [UsageRow(other)]);

        Assert.Equal([t0, t1], (await GetLoadedAsync(printerId)).Select(l => l.Filament!.Id));
    }

    #endregion

    #region Usage rows

    [Fact]
    public async Task AddPrint_KeepsEachUsageRowsSlot()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);

        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 1), UsageRow(null, slot: 3)]);

        Assert.Equal([1, 3], print.FilamentUsage!.Select(u => u.Slot).Order());
    }

    [Fact]
    public async Task Complete_AnUnlinkedRowBySlot_UpdatesThatSlotsRow()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 0), UsageRow(null, slot: 1)]);

        var completed = await CompleteAsync(print.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 1, LengthInM = 2.5 }],
        });

        Assert.Equal(2, completed.FilamentUsage!.Count);
        Assert.Equal(2.5, completed.FilamentUsage.Single(u => u.Slot == 1).LengthInM);
        Assert.Null(completed.FilamentUsage.Single(u => u.Slot == 0).LengthInM);
    }

    [Fact]
    public async Task Complete_ASlotNotOnThePrint_AddsARowForIt()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var print = await CreatePrintAsync(printerId, [UsageRow(null, slot: 0)]);

        var completed = await CompleteAsync(print.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 2, LengthInM = 1.5 }],
        });

        Assert.Equal([0, 2], completed.FilamentUsage!.Select(u => u.Slot).Order());
    }

    [Fact]
    public async Task Complete_TheSameSlotTwiceInOneBody_IsBadRequest()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var print = await CreatePrintAsync(printerId, []);

        var response = await SendAsync(HttpMethod.Post, $"/api/Prints/{print.Id}/complete", new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage =
            [
                new CompletePrintFilamentUsageDto { Slot = 1, LengthInM = 1 },
                new CompletePrintFilamentUsageDto { Slot = 1, LengthInM = 2 },
            ],
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Print events

    [Fact]
    public async Task StartedEvent_TakesTheSpoolInEachRowsSlot()
    {
        var printerId = await CreatePrinterAsync(slotCount: 4);
        var (t0, t2) = (await CreateFilamentAsync(), await CreateFilamentAsync());
        await LoadSlotAsync(printerId, 2, t2);
        await LoadSlotAsync(printerId, 0, t0);

        var print = await StartAsync(printerId, slots: [2, 1]);

        var rows = print.FilamentUsage!.OrderBy(u => u.Slot).ToList();
        Assert.Equal([1, 2], rows.Select(r => r.Slot));
        Assert.Null(rows[0].FilamentId); // nothing loaded in slot 1
        Assert.Equal(t2, rows[1].FilamentId);
    }

    [Fact]
    public async Task StartedEvent_SlotZeroOnASingleToolPrinter_TakesItsSpool()
    {
        // The notifier always reports slot 0, and its spool was loaded with no slot.
        var printerId = await CreatePrinterAsync(slotCount: 1);
        var spool = await CreateFilamentAsync();
        await PutPrinterAsync(printerId, loaded: [spool]);

        var print = await StartAsync(printerId, slots: [0]);

        Assert.Equal(spool, Assert.Single(print.FilamentUsage!).FilamentId);
    }

    #endregion

    private static PrintFilamentSummaryDto UsageRow(Guid? filamentId, int? slot = null) => new()
    {
        Id = Guid.NewGuid(),
        Filament = filamentId is { } id ? new FilamentSummaryDto { Id = id } : null,
        Slot = slot,
        EstimatedSource = PrintFilament.SourceMeasurement.Length,
        EstimatedLengthInM = 1,
        Source = PrintFilament.SourceMeasurement.Length,
    };

    private async Task<Print> StartAsync(long printerId, int[] slots)
    {
        using var scope = _factory.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IPrintEventService>();
        var result = await events.Started(new PrintStartedEvent
        {
            UserId = IntegrationTestSeeder.TestUserId,
            PrinterId = printerId,
            Source = PrintSource.Moonraker,
            ExternalSource = "slot-test",
            ExternalId = Guid.NewGuid().ToString("N"),
            Title = "Slot test",
            FileName = "slot-test.gcode",
            StartDate = DateTimeOffset.UtcNow,
            Usage = slots
                .Select(slot => new PrintEventUsage(
                    slot, PrintFilament.SourceMeasurement.Length, 1.0, PrintFilament.SourceMeasurement.Length, null, ""))
                .ToList(),
        });
        return result.Print;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken))!;
    }

    private Task<HttpResponseMessage> LoadSlotAsync(long printerId, int slot, Guid filamentId, string? slotLabel = null) =>
        SendAsync(HttpMethod.Put, $"/api/Printers/{printerId}/slots/{slot}",
            new LoadPrinterSlotDto { FilamentId = filamentId, SlotLabel = slotLabel });

    private async Task<List<PrinterFilamentSummaryDto>> GetLoadedAsync(long printerId) =>
        await ReadAsync<List<PrinterFilamentSummaryDto>>(await SendAsync(HttpMethod.Get, $"/api/Printers/{printerId}/filament"));

    private async Task<PrinterDetailDto> GetPrinterAsync(long printerId) =>
        await ReadAsync<PrinterDetailDto>(await SendAsync(HttpMethod.Get, $"/api/Printers/{printerId}"));

    private async Task<long> CreatePrinterAsync(int? slotCount, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Printers", new AddPrinterDTO
        {
            Name = $"Slot printer {Guid.NewGuid():N}",
            Make = "Snapmaker",
            Model = "U1",
            IsActive = true,
            SlotCount = slotCount,
        }, userOAuthId);
        return (await ReadAsync<PrinterDetailDto>(response)).Id!.Value;
    }

    private async Task<HttpResponseMessage> PutPrinterAsync(long printerId, Guid[] loaded, int? slotCount = null)
    {
        var current = await GetPrinterAsync(printerId);
        return await SendAsync(HttpMethod.Put, $"/api/Printers/{printerId}", new AddPrinterDTO
        {
            Id = printerId,
            Name = current.Name,
            Make = current.Make,
            Model = current.Model,
            IsActive = true,
            SlotCount = slotCount,
            LoadedFilaments = loaded
                .Select(id => new AddPrinterFilamentDto
                {
                    Id = current.LoadedFilaments!.FirstOrDefault(l => l.Filament?.Id == id)?.Id ?? Guid.Empty,
                    FilamentId = id,
                })
                .ToList(),
        });
    }

    private async Task<PrintDetailDTO> CreatePrintAsync(long printerId, ICollection<PrintFilamentSummaryDto> usage) =>
        await ReadAsync<PrintDetailDTO>(await SendAsync(HttpMethod.Post, "/api/Prints", new AddPrintDTO
        {
            Title = $"slot print {Guid.NewGuid():N}",
            PrinterId = printerId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            FilamentUsage = usage,
        }));

    private async Task<PrintDetailDTO> CompleteAsync(long printId, CompletePrintDto body) =>
        await ReadAsync<PrintDetailDTO>(await SendAsync(HttpMethod.Post, $"/api/Prints/{printId}/complete", body));

    private async Task<Guid> CreateFilamentAsync(string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Filaments", new AddFilamentDto
        {
            DisplayName = $"Slot spool {Guid.NewGuid():N}",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Green",
            ColorHex = "00FF00",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
        }, userOAuthId);
        return (await ReadAsync<FilamentDetailDto>(response)).Id;
    }
}
