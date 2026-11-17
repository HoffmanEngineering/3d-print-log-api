using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Claim (#165): the slicer uploader, the Cura plugin or a hand entry creates a print in Printing,
/// and a connector that only sees the job adopts that print instead of creating a second one.
/// </summary>
public class PrintsControllerClaimTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient = factory.CreateClient();

    #region Claim

    [Fact]
    public async Task Claim_UnclaimedPrintingPrint_SetsTheExternalPair()
    {
        var created = await CreatePrintAsync();
        var externalId = UniqueExternalId();

        var response = await ClaimAsync(created.Id, Claim(externalId, source: " MoonRaker "));

        var claimed = await ReadAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Id, claimed.Id);
        Assert.Equal("moonraker", claimed.ExternalSource);
        Assert.Equal(externalId, claimed.ExternalId);
        Assert.Equal(created.Title, claimed.Title);
        Assert.Equal(PrintStatus.Printing, claimed.Status);
    }

    [Fact]
    public async Task Claim_KeepsTheOriginalSource()
    {
        // The UI shows how the print was made and, separately, which connector finished it.
        var created = await CreatePrintAsync();
        await UpdatePrintAsync(created.Id, p => p.Source = PrintSource.SlicerPlugin);

        await ReadAsync(await ClaimAsync(created.Id, Claim(UniqueExternalId())));

        Assert.Equal(PrintSource.SlicerPlugin, (await LoadPrintAsync(created.Id)).Source);
    }

    [Fact]
    public async Task Claim_SamePairAgain_ReturnsThePrintUnchanged()
    {
        var created = await CreatePrintAsync();
        var externalId = UniqueExternalId();
        await ReadAsync(await ClaimAsync(created.Id, Claim(externalId)));

        var retry = await ClaimAsync(created.Id, Claim(externalId));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(externalId, (await ReadAsync(retry)).ExternalId);
    }

    [Fact]
    public async Task Claim_SamePairInADifferentCase_ReplaysThePrint()
    {
        // Ids are matched case-insensitively (#204), because SQL Server's default collation already
        // matches them that way in FindPrintIdByExternalPair. A case-sensitive replay check here
        // gave such a retry 409 where the same pair on create replayed.
        var created = await CreatePrintAsync();
        var externalId = $"Job-{UniqueExternalId()}";
        await ReadAsync(await ClaimAsync(created.Id, Claim(externalId)));

        var retry = await ClaimAsync(created.Id, Claim(externalId.ToUpperInvariant(), source: "MOONRAKER"));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var replayed = await ReadAsync(retry);
        Assert.Equal(created.Id, replayed.Id);
        // A replay changes nothing, so the stored id keeps the case of the first claim.
        Assert.Equal(externalId, replayed.ExternalId);
    }

    [Fact]
    public async Task Claim_SamePairAfterThePrintFinished_StillReturnsThePrint()
    {
        // A claim that timed out client-side but landed can be retried after the job ended.
        var created = await CreatePrintAsync();
        var externalId = UniqueExternalId();
        await ReadAsync(await ClaimAsync(created.Id, Claim(externalId)));
        await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto { Status = PrintStatus.Success }));

        var retry = await ClaimAsync(created.Id, Claim(externalId));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(PrintStatus.Success, (await ReadAsync(retry)).Status);
    }

    [Fact]
    public async Task Claim_DifferentPair_IsConflictAndKeepsTheFirst()
    {
        var created = await CreatePrintAsync();
        var first = UniqueExternalId();
        await ReadAsync(await ClaimAsync(created.Id, Claim(first)));

        var response = await ClaimAsync(created.Id, Claim(UniqueExternalId()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(first, (await GetDetailAsync(created.Id)).ExternalId);
    }

    [Fact]
    public async Task Claim_PrintCreatedWithAPair_IsConflict()
    {
        var dto = NewPrint();
        dto.ExternalSource = "octoprint";
        dto.ExternalId = UniqueExternalId();
        var created = await ReadAsync(await PostAsync(dto));

        var response = await ClaimAsync(created.Id, Claim(UniqueExternalId()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(PrintStatus.Pending)]
    [InlineData(PrintStatus.Success)]
    [InlineData(PrintStatus.Failed)]
    public async Task Claim_PrintNotPrinting_IsConflict(PrintStatus status)
    {
        var created = await CreatePrintAsync(status);

        var response = await ClaimAsync(created.Id, Claim(UniqueExternalId()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null((await GetDetailAsync(created.Id)).ExternalId);
    }

    [Fact]
    public async Task Claim_PairAlreadyOnAnotherPrint_IsConflictNamingThatPrint()
    {
        var externalId = UniqueExternalId();
        var holderDto = NewPrint();
        holderDto.ExternalSource = "moonraker";
        holderDto.ExternalId = externalId;
        var holder = await ReadAsync(await PostAsync(holderDto));
        var created = await CreatePrintAsync();

        var response = await ClaimAsync(created.Id, Claim(externalId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(holder.Id, problem.RootElement.GetProperty("printId").GetInt64());
        Assert.Null((await GetDetailAsync(created.Id)).ExternalId);
    }

    [Fact]
    public async Task Claim_ThenCreateWithTheSamePair_ReplaysTheClaimedPrint()
    {
        // A connector that claimed, lost its state and falls back to creating still gets one print.
        var created = await CreatePrintAsync();
        var externalId = UniqueExternalId();
        await ReadAsync(await ClaimAsync(created.Id, Claim(externalId)));

        var dto = NewPrint();
        dto.ExternalSource = "moonraker";
        dto.ExternalId = externalId;
        var response = await PostAsync(dto);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Id, (await ReadAsync(response)).Id);
    }

    [Fact]
    public async Task Claim_AnotherUsersPrint_IsForbidden()
    {
        var created = await CreatePrintAsync();

        var response = await ClaimAsync(created.Id, Claim(UniqueExternalId()), IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await GetDetailAsync(created.Id)).ExternalId);
    }

    [Fact]
    public async Task Claim_MissingPrint_IsNotFound()
    {
        var response = await ClaimAsync(long.MaxValue, Claim(UniqueExternalId()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("moonraker", null)]
    [InlineData(null, "job:1")]
    [InlineData(" ", "job:1")]
    [InlineData("MCP", "job:1")]
    public async Task Claim_InvalidPair_IsBadRequest(string? source, string? externalId)
    {
        var created = await CreatePrintAsync();

        var response = await ClaimAsync(created.Id, new ClaimPrintDto { ExternalSource = source, ExternalId = externalId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Claim_StartDate_FillsOnlyAMissingOne()
    {
        var jobStart = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var withoutStart = await CreatePrintAsync();
        var userStart = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        var withStart = await CreatePrintAsync(startDate: userStart);

        var filled = await ReadAsync(await ClaimAsync(withoutStart.Id, Claim(UniqueExternalId(), startDate: jobStart)));
        var kept = await ReadAsync(await ClaimAsync(withStart.Id, Claim(UniqueExternalId(), startDate: jobStart)));

        Assert.Equal(jobStart, filled.StartDate);
        Assert.Equal(userStart, kept.StartDate);
    }

    [Fact]
    public async Task Claim_OwnConnectionInstanceId_LinksTheConnection()
    {
        var instanceId = Guid.NewGuid().ToString();
        await RegisterConnectionAsync(instanceId, "Claiming bridge");
        var created = await CreatePrintAsync();

        var claimed = await ReadAsync(await ClaimAsync(created.Id, Claim(UniqueExternalId(), connectionInstanceId: instanceId)));

        Assert.Equal("Claiming bridge", claimed.ConnectionDisplayName);
    }

    [Fact]
    public async Task Claim_UnknownConnectionInstanceId_IsIgnored()
    {
        var created = await CreatePrintAsync();

        var response = await ClaimAsync(created.Id, Claim(UniqueExternalId(), connectionInstanceId: Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await ReadAsync(response)).ConnectionDisplayName);
    }

    [Fact]
    public async Task Claim_ThenComplete_MergesUsageBySlot()
    {
        // The uploader logs estimates per tool with no spool; the connector finishes with the
        // spool it saw on each slot (#162 rules).
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(usage:
        [
            new PrintFilamentSummaryDto
            {
                Id = Guid.NewGuid(),
                Slot = 0,
                EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                EstimatedAmountMg = 12_000,
                Notes = "from the uploader",
            },
        ]);
        await ReadAsync(await ClaimAsync(created.Id, Claim(UniqueExternalId())));

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = filament.Id, AmountMg = 11_500 }],
        }));

        Assert.Equal(PrintStatus.Success, completed.Status);
        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(filament.Id, row.Filament?.Id);
        Assert.Equal(12_000, row.EstimatedAmountMg);
        Assert.Equal(11_500, row.AmountMg);
        Assert.Equal("from the uploader", row.Notes);
    }

    [Fact]
    public async Task Complete_SingleToolPrinter_UnslottedEstimateIsSlotZeros()
    {
        // The Slic3r uploader writes its estimate with no slot; the bridge completes the one tool
        // as slot 0 with the loaded spool. That fills the estimate row instead of adding a second.
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId, slotCount: 1);
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(printerId: printerId, usage: [UploaderEstimateRow(12_000)]);
        await ReadAsync(await ClaimAsync(created.Id, Claim(UniqueExternalId())));
        var completion = new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = filament.Id, LengthInM = 4.0 }],
        };

        await ReadAsync(await CompleteAsync(created.Id, completion));
        var completed = await ReadAsync(await CompleteAsync(created.Id, completion));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(filament.Id, row.Filament?.Id);
        Assert.Equal(0, row.Slot);
        Assert.Equal(12_000, row.EstimatedAmountMg);
        Assert.Equal(4.0, row.LengthInM);
        Assert.Equal(PrintFilament.SourceMeasurement.Length, row.Source);
        Assert.Equal("from the uploader", row.Notes);
    }

    [Fact]
    public async Task Complete_SingleToolPrinter_SlotZeroWithNoSpool_FillsTheUnslottedRow()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId, slotCount: 1);
        var created = await CreatePrintAsync(printerId: printerId, usage: [UploaderEstimateRow(12_000)]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 0, AmountMg = 11_000 }],
        }));

        var row = Assert.Single(completed.FilamentUsage!);
        Assert.Equal(0, row.Slot);
        Assert.Equal(12_000, row.EstimatedAmountMg);
        Assert.Equal(11_000, row.AmountMg);
    }

    [Fact]
    public async Task Complete_MultiToolPrinter_UnslottedRowIsNotTakenBySlotZero()
    {
        // With more than one tool an unslotted estimate could belong to any of them.
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId, slotCount: 2);
        var filament = await CreateFilamentAsync();
        var created = await CreatePrintAsync(printerId: printerId, usage: [UploaderEstimateRow(12_000)]);

        var completed = await ReadAsync(await CompleteAsync(created.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            FilamentUsage = [new CompletePrintFilamentUsageDto { Slot = 0, FilamentId = filament.Id, AmountMg = 11_000 }],
        }));

        Assert.Equal(2, completed.FilamentUsage!.Count);
        var estimate = Assert.Single(completed.FilamentUsage, r => r.Filament == null);
        Assert.Null(estimate.Slot);
        Assert.Null(estimate.AmountMg);
    }

    #endregion

    #region Claimable

    [Fact]
    public async Task Claimable_ReturnsOnlyTheCallersUnclaimedPrintingPrintsOnThatPrinter()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var candidate = await CreatePrintAsync(printerId: printerId);
        var claimed = await CreatePrintAsync(printerId: printerId);
        await ReadAsync(await ClaimAsync(claimed.Id, Claim(UniqueExternalId())));
        var finished = await CreatePrintAsync(PrintStatus.Success, printerId: printerId);
        var otherPrinter = await CreatePrintAsync();
        var withPair = NewPrint(printerId);
        withPair.ExternalSource = "octoprint";
        withPair.ExternalId = UniqueExternalId();
        await ReadAsync(await PostAsync(withPair));

        var result = await GetClaimableAsync($"printerId={printerId}");

        var only = Assert.Single(result);
        Assert.Equal(candidate.Id, only.Id);
        Assert.Equal(printerId, only.PrinterId);
        Assert.DoesNotContain(result, p => p.Id == finished.Id || p.Id == otherPrinter.Id);
    }

    [Fact]
    public async Task Claimable_AnotherUsersPrinter_IsEmpty()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        await CreatePrintAsync(printerId: printerId);

        var result = await GetClaimableAsync($"printerId={printerId}", IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Claimable_CarriesWhatAConnectorMatchesOn()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var created = await CreatePrintAsync(printerId: printerId, fileName: "benchy.gcode", usage:
        [
            new PrintFilamentSummaryDto
            {
                Id = Guid.NewGuid(),
                Slot = 1,
                EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                EstimatedAmountMg = 4_000,
            },
        ]);
        var hash = Enumerable.Range(0, 20).Select(i => (byte)(i * 13)).ToArray();
        await UpdatePrintAsync(created.Id, p =>
        {
            p.FileHash = hash;
            p.Source = PrintSource.SlicerPlugin;
            p.Slicer = "orcaslicer";
        });

        var print = Assert.Single(await GetClaimableAsync($"printerId={printerId}"));

        Assert.Equal("benchy.gcode", print.FileName);
        Assert.Equal(Convert.ToHexStringLower(hash), print.FileHash);
        Assert.Equal(PrintSource.SlicerPlugin, print.Source);
        Assert.True(print.HasSlicerSettings);
        Assert.Equal(PrintStatus.Printing, print.Status);
        Assert.True(print.CreatedDate > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(1, Assert.Single(print.FilamentUsage!).Slot);
    }

    [Fact]
    public async Task Claimable_HandEnteredPrint_HasNoSlicerSettings()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        await CreatePrintAsync(printerId: printerId);

        var print = Assert.Single(await GetClaimableAsync($"printerId={printerId}"));

        Assert.False(print.HasSlicerSettings);
        Assert.Null(print.FileHash);
    }

    [Fact]
    public async Task Claimable_CreatedAfter_LeavesOutOlderPrints()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var old = await CreatePrintAsync(printerId: printerId);
        await BackdatePrintAsync(old.Id, DateTime.UtcNow.AddHours(-3));
        var recent = await CreatePrintAsync(printerId: printerId);
        var after = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));

        var result = await GetClaimableAsync($"printerId={printerId}&createdAfter={after}");

        Assert.Equal(recent.Id, Assert.Single(result).Id);
    }

    [Fact]
    public async Task Claimable_NewestFirst_AndLimited()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var older = await CreatePrintAsync(printerId: printerId);
        await BackdatePrintAsync(older.Id, DateTime.UtcNow.AddMinutes(-10));
        var newer = await CreatePrintAsync(printerId: printerId);

        var all = await GetClaimableAsync($"printerId={printerId}");
        var limited = await GetClaimableAsync($"printerId={printerId}&limit=1");

        Assert.Equal([newer.Id, older.Id], all.Select(p => p.Id));
        Assert.Equal(newer.Id, Assert.Single(limited).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("printerId=1&limit=0")]
    [InlineData("printerId=1&limit=101")]
    public async Task Claimable_BadQuery_IsBadRequest(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/claimable?{query}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    /// <summary>The Slic3r uploader's estimate row: no spool and, on a single-tool printer, no slot.</summary>
    private static PrintFilamentSummaryDto UploaderEstimateRow(int estimatedMg) => new()
    {
        Id = Guid.NewGuid(),
        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
        EstimatedAmountMg = estimatedMg,
        Notes = "from the uploader",
    };

    private static string UniqueExternalId() => $"{Guid.NewGuid()}:{Random.Shared.Next(1, 100000)}";

    private static ClaimPrintDto Claim(
        string externalId, string source = "moonraker", DateTimeOffset? startDate = null, string? connectionInstanceId = null) => new()
        {
            ExternalSource = source,
            ExternalId = externalId,
            StartDate = startDate,
            ConnectionInstanceId = connectionInstanceId,
        };

    private static AddPrintDTO NewPrint(long? printerId = null) => new()
    {
        Title = $"claim {Guid.NewGuid():N}",
        PrinterId = printerId ?? IntegrationTestSeeder.TestPrinterId,
        Status = PrintStatus.Printing,
        ViewStatus = PrintViewStatus.Private,
        FilamentUsage = [],
    };

    private async Task<PrintDetailDTO> CreatePrintAsync(
        PrintStatus status = PrintStatus.Printing,
        long? printerId = null,
        DateTimeOffset? startDate = null,
        string? fileName = null,
        ICollection<PrintFilamentSummaryDto>? usage = null)
    {
        var dto = NewPrint(printerId);
        dto.Status = status;
        dto.StartDate = startDate;
        dto.FileName = fileName;
        dto.FilamentUsage = usage ?? [];
        return await ReadAsync(await PostAsync(dto));
    }

    private async Task<HttpResponseMessage> PostAsync(AddPrintDTO dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(dto);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> ClaimAsync(long id, ClaimPrintDto body, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{id}/claim");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> CompleteAsync(long id, CompletePrintDto body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{id}/complete");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<List<ClaimablePrintDto>> GetClaimableAsync(string query, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/claimable?{query}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<List<ClaimablePrintDto>>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> GetDetailAsync(long id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));
    }

    /// <summary>Sets what no endpoint writes directly: provenance, the file hash.</summary>
    private async Task UpdatePrintAsync(long id, Action<Print> update)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = await context.Prints.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        update(print);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Backdates a print's creation. SaveChanges never lets an update change CreatedDate (#256),
    /// so this bypasses the change tracker.
    /// </summary>
    private async Task BackdatePrintAsync(long id, DateTime createdUtc)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        await context.Prints.Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedDate, createdUtc), TestContext.Current.CancellationToken);
    }

    private async Task<Print> LoadPrintAsync(long id)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.Prints.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task RegisterConnectionAsync(string instanceId, string displayName)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new PutConnectionDto
        {
            Kind = "moonraker",
            DisplayName = displayName,
            AgentVersion = "0.1.0",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<FilamentDetailDto> CreateFilamentAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Filaments");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddFilamentDto
        {
            DisplayName = $"Claim test {Guid.NewGuid():N}",
            Brand = "Test Brand",
            MaterialType = "PLA",
            ColorName = "Green",
            ColorHex = "00FF00",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FilamentDetailDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CreatePrinterAsync(string userOAuthId, int? slotCount = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO { Name = "Claim test printer", Make = "Make", Model = "Model", IsActive = true, SlotCount = slotCount });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }
}
