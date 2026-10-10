using System.Net;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// External ids (#144): a connector that retries, replays an offline queue or backfills history
/// posts the same (externalSource, externalId) pair more than once, and must get one print.
/// </summary>
public class PrintsControllerExternalIdTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;

    public PrintsControllerExternalIdTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    private static AddPrintDTO NewExternalPrint(string externalId, string title = "benchy.gcode") => new()
    {
        Title = title,
        PrinterId = IntegrationTestSeeder.TestPrinterId,
        Status = PrintStatus.Printing,
        ViewStatus = PrintViewStatus.Private,
        ExternalSource = "moonraker",
        ExternalId = externalId,
    };

    private async Task<HttpResponseMessage> PostAsync(AddPrintDTO dto, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(dto);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string UniqueExternalId() => $"{Guid.NewGuid()}:{Random.Shared.Next(1, 100000)}";

    [Fact]
    public async Task PostPrint_SameExternalPairTwice_SecondReturnsOkWithTheSamePrint()
    {
        var externalId = UniqueExternalId();

        var first = await PostAsync(NewExternalPrint(externalId));
        var second = await PostAsync(NewExternalPrint(externalId));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstPrint = (await first.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
        var secondPrint = (await second.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
        Assert.Equal(firstPrint.Id, secondPrint.Id);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
    }

    [Fact]
    public async Task PostPrint_SameExternalPair_DifferentUsers_EachGetTheirOwnPrint()
    {
        var externalId = UniqueExternalId();
        var secondaryPrinterId = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);

        var mine = await PostAsync(NewExternalPrint(externalId));
        var other = NewExternalPrint(externalId);
        other.PrinterId = secondaryPrinterId;
        var theirs = await PostAsync(other, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Created, theirs.StatusCode);
        Assert.NotEqual(
            (await ReadAsync(mine)).Id,
            (await ReadAsync(theirs)).Id);
    }

    [Fact]
    public async Task GetPrintById_Owner_SeesTheExternalPair()
    {
        var externalId = UniqueExternalId();
        var created = await ReadAsync(await PostAsync(NewExternalPrint(externalId)));

        var detail = await GetDetailAsync(created.Id, IntegrationTestSeeder.TestUserOAuthId);

        Assert.Equal("moonraker", detail.ExternalSource);
        Assert.Equal(externalId, detail.ExternalId);
    }

    [Fact]
    public async Task GetPrintById_PublicPrintViewedByAnotherUser_HidesTheExternalPair()
    {
        // The id embeds the connector's instance UUID, which says nothing a visitor needs.
        var dto = NewExternalPrint(UniqueExternalId());
        dto.ViewStatus = PrintViewStatus.Public;
        var created = await ReadAsync(await PostAsync(dto));

        var detail = await GetDetailAsync(created.Id, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Null(detail.ExternalSource);
        Assert.Null(detail.ExternalId);
    }

    [Fact]
    public async Task GetPrintSummary_IncludesTheExternalPair()
    {
        var externalId = UniqueExternalId();
        var created = await ReadAsync(await PostAsync(NewExternalPrint(externalId, title: $"summary {externalId}")));

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/summary?searchText={Uri.EscapeDataString(externalId)}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var page = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(TestContext.Current.CancellationToken))!;

        var row = Assert.Single(page.Items, p => p.Id == created.Id);
        Assert.Equal("moonraker", row.ExternalSource);
        Assert.Equal(externalId, row.ExternalId);
    }

    [Fact]
    public async Task GetPrintSummary_AnotherUsersPublicPrints_HideTheExternalPair()
    {
        var externalId = UniqueExternalId();
        var dto = NewExternalPrint(externalId, title: $"public {externalId}");
        dto.ViewStatus = PrintViewStatus.Public;
        var created = await ReadAsync(await PostAsync(dto));

        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/summary?userId={IntegrationTestSeeder.TestUserId}&searchText={Uri.EscapeDataString(externalId)}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.SecondaryUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        var page = (await response.Content.ReadFromJsonAsync<PagedList<PrintSummaryDTO>>(TestContext.Current.CancellationToken))!;

        var row = Assert.Single(page.Items, p => p.Id == created.Id);
        Assert.Null(row.ExternalSource);
        Assert.Null(row.ExternalId);
    }

    [Fact]
    public async Task GetByExternal_OwnPair_ReturnsThePrint()
    {
        var externalId = UniqueExternalId();
        var created = await ReadAsync(await PostAsync(NewExternalPrint(externalId)));

        var response = await GetByExternalAsync("moonraker", externalId, IntegrationTestSeeder.TestUserOAuthId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Id, (await ReadAsync(response)).Id);
    }

    [Fact]
    public async Task GetByExternal_UnknownPair_Returns404()
    {
        var response = await GetByExternalAsync("moonraker", UniqueExternalId(), IntegrationTestSeeder.TestUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetByExternal_AnotherUsersPair_Returns404()
    {
        var externalId = UniqueExternalId();
        await PostAsync(NewExternalPrint(externalId));

        var response = await GetByExternalAsync("moonraker", externalId, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostPrint_Replay_AppliesConnectorOwnedFields()
    {
        var externalId = UniqueExternalId();
        await PostAsync(NewExternalPrint(externalId));

        var finished = NewExternalPrint(externalId);
        finished.Status = PrintStatus.Success;
        finished.PrintTimeInSeconds = 3600;
        finished.EstimatedPrintTimeInSeconds = 3500;
        finished.FilamentUsageMg = 12_000;
        var replay = await PostAsync(finished);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var detail = await GetDetailAsync((await ReadAsync(replay)).Id, IntegrationTestSeeder.TestUserOAuthId);
        Assert.Equal(PrintStatus.Success, detail.Status);
        Assert.Equal(3600, detail.PrintTimeInSeconds);
        Assert.Equal(3500, detail.EstimatedPrintTimeInSeconds);
        Assert.Equal(12_000, detail.FilamentUsageMg);
    }

    [Fact]
    public async Task PostPrint_Replay_StartRetriedAfterFinish_DoesNotReopenThePrint()
    {
        // A start call that timed out client-side but landed can be retried after the finish.
        var externalId = UniqueExternalId();
        await PostAsync(NewExternalPrint(externalId));
        var finished = NewExternalPrint(externalId);
        finished.Status = PrintStatus.Success;
        await PostAsync(finished);

        var lateStart = await PostAsync(NewExternalPrint(externalId));

        var detail = await GetDetailAsync((await ReadAsync(lateStart)).Id, IntegrationTestSeeder.TestUserOAuthId);
        Assert.Equal(PrintStatus.Success, detail.Status);
    }

    [Fact]
    public async Task PostPrint_Replay_OmittedConnectorFields_KeepTheirStoredValues()
    {
        var externalId = UniqueExternalId();
        var start = NewExternalPrint(externalId);
        start.EstimatedPrintTimeInSeconds = 3500;
        start.FilamentUsage = [UsageRow(estimatedMg: 20_000, actualMg: null)];
        await PostAsync(start);

        var finished = NewExternalPrint(externalId);
        finished.Status = PrintStatus.Success;
        finished.PrintTimeInSeconds = 3600;
        var replay = await PostAsync(finished);

        var detail = await GetDetailAsync((await ReadAsync(replay)).Id, IntegrationTestSeeder.TestUserOAuthId);
        Assert.Equal(3500, detail.EstimatedPrintTimeInSeconds);
        var row = Assert.Single(detail.FilamentUsage!);
        Assert.Equal(20_000, row.EstimatedAmountMg);
    }

    [Fact]
    public async Task PostPrint_Replay_LeavesUserOwnedFieldsAlone()
    {
        // A user who renamed the print or added notes while it ran keeps those edits.
        var externalId = UniqueExternalId();
        await PostAsync(NewExternalPrint(externalId, title: "original title"));

        var replayDto = NewExternalPrint(externalId, title: "connector title");
        replayDto.Notes = "connector notes";
        var replay = await PostAsync(replayDto);

        var detail = await GetDetailAsync((await ReadAsync(replay)).Id, IntegrationTestSeeder.TestUserOAuthId);
        Assert.Equal("original title", detail.Title);
        Assert.Null(detail.Notes);
    }

    [Fact]
    public async Task PostPrint_Replay_ReplacesFilamentUsageRows()
    {
        var externalId = UniqueExternalId();
        var start = NewExternalPrint(externalId);
        start.FilamentUsage = [UsageRow(estimatedMg: 20_000, actualMg: null)];
        await PostAsync(start);

        var end = NewExternalPrint(externalId);
        end.FilamentUsage = [UsageRow(estimatedMg: 20_000, actualMg: 25_000)];
        var replay = await PostAsync(end);

        var detail = await GetDetailAsync((await ReadAsync(replay)).Id, IntegrationTestSeeder.TestUserOAuthId);
        var row = Assert.Single(detail.FilamentUsage!);
        Assert.Equal(25_000, row.AmountMg);
    }

    [Fact]
    public async Task PutPrint_KeepsTheExternalPair()
    {
        var externalId = UniqueExternalId();
        var created = await ReadAsync(await PostAsync(NewExternalPrint(externalId)));

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/Prints/{created.Id}");
        put.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        put.Content = JsonContent.Create(new PutPrintDetailDto
        {
            Id = created.Id,
            Title = "renamed",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Private,
        });
        var putResponse = await _httpClient.SendAsync(put, TestContext.Current.CancellationToken);
        Assert.True(putResponse.IsSuccessStatusCode, await putResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var detail = await GetDetailAsync(created.Id, IntegrationTestSeeder.TestUserOAuthId);
        Assert.Equal("renamed", detail.Title);
        Assert.Equal("moonraker", detail.ExternalSource);
        Assert.Equal(externalId, detail.ExternalId);
    }

    [Theory]
    [InlineData("moonraker", null)]
    [InlineData(null, "abc:1")]
    [InlineData("  ", "abc:1")]
    [InlineData("moonraker", "  ")]
    public async Task PostPrint_IncompleteExternalPair_Returns400(string? source, string? externalId)
    {
        var dto = NewExternalPrint("unused");
        dto.ExternalSource = source;
        dto.ExternalId = externalId;

        var response = await PostAsync(dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostPrint_ReservedMcpSource_Returns400()
    {
        // "mcp" belongs to create_print's idempotency keys. A REST caller using it could replay,
        // and so read back, a print an agent created under that key.
        var dto = NewExternalPrint(UniqueExternalId());
        dto.ExternalSource = "MCP";

        var response = await PostAsync(dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static PrintFilamentSummaryDto UsageRow(int estimatedMg, int? actualMg) => new()
    {
        Id = Guid.NewGuid(),
        Filament = new FilamentSummaryDto { Id = IntegrationTestSeeder.TestFilamentId1 },
        EstimatedSource = PrintFilament.SourceMeasurement.Weight,
        EstimatedAmountMg = estimatedMg,
        Source = actualMg.HasValue ? PrintFilament.SourceMeasurement.Weight : null,
        AmountMg = actualMg,
    };

    private async Task<PrintDetailDTO> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> GetDetailAsync(long id, string userOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        return await ReadAsync(await _httpClient.SendAsync(request, TestContext.Current.CancellationToken));
    }

    private async Task<HttpResponseMessage> GetByExternalAsync(string source, string externalId, string userOAuthId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/Prints/external?source={Uri.EscapeDataString(source)}&id={Uri.EscapeDataString(externalId)}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<long> CreatePrinterAsync(string userOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO { Name = "External id printer", Make = "Make", Model = "Model", IsActive = true });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }
}
