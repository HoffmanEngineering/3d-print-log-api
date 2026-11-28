using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services.Timeline;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Timeline;

/// <summary>The lifecycle entries the existing create, finish and claim paths write (api#343).</summary>
public class PrintTimelineLifecycleTests : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PrintTimelineLifecycleTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreatedPrinting_RecordsStarted()
    {
        var start = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var print = await CreateAsync(PrintStatus.Printing, startDate: start);

        var row = Assert.Single(await RowsAsync(print.Id));
        Assert.Equal(PrintTimelineEventType.Started, row.Type);
        Assert.Equal(start, row.OccurredAt);
        Assert.Equal(PrintSource.Web, row.Source);
    }

    [Fact]
    public async Task CreatedFinished_RecordsNothing()
    {
        var print = await CreateAsync(PrintStatus.Success);

        Assert.Empty(await RowsAsync(print.Id));
    }

    [Fact]
    public async Task MoonrakerNotifier_StartAndComplete_RecordStartedAndFinished()
    {
        var file = $"lifecycle-{Guid.NewGuid():N}.gcode";
        await NotifyAsync(new PrintEventMessageDto { EventName = "started", Filename = file, PrinterId = IntegrationTestSeeder.TestPrinterId });
        await NotifyAsync(new PrintEventMessageDto
        {
            EventName = "complete", Filename = file, PrinterId = IntegrationTestSeeder.TestPrinterId, TotalDuration = 60,
        });

        var printId = await PrintIdByFileAsync(file);
        var rows = await RowsAsync(printId);
        Assert.Equal([PrintTimelineEventType.Started, PrintTimelineEventType.Finished], rows.Select(r => r.Type));
        Assert.All(rows, r => Assert.Equal(PrintSource.Moonraker, r.Source));
        Assert.Equal(nameof(PrintStatus.Success), rows[1].Code);
    }

    [Fact]
    public async Task CompleteFailed_WithFailure_RecordsFinishedAndFailure()
    {
        var print = await CreateAsync(PrintStatus.Printing);
        var ended = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());

        await CompleteAsync(print.Id, new CompletePrintDto
        {
            Status = PrintStatus.Failed,
            EndedAt = ended,
            Failure = new CompletePrintFailureDto { Code = "klippy_shutdown", Message = "Klipper shutdown" },
        });

        var rows = await RowsAsync(print.Id);
        var finished = Assert.Single(rows, r => r.Type == PrintTimelineEventType.Finished);
        Assert.Equal(nameof(PrintStatus.Failed), finished.Code);
        Assert.Equal(ended, finished.OccurredAt);
        var failure = Assert.Single(rows, r => r.Type == PrintTimelineEventType.Failure);
        Assert.Equal("klippy_shutdown", failure.Code);
        Assert.Equal("Klipper shutdown", failure.Message);
    }

    [Fact]
    public async Task CompleteSuccess_WithFailure_RecordsNoFailure()
    {
        var print = await CreateAsync(PrintStatus.Printing);

        await CompleteAsync(print.Id, new CompletePrintDto
        {
            Status = PrintStatus.Success,
            Failure = new CompletePrintFailureDto { Code = "x" },
        });

        Assert.DoesNotContain(await RowsAsync(print.Id), r => r.Type == PrintTimelineEventType.Failure);
    }

    [Fact]
    public async Task ReplayedComplete_RecordsOneFinished()
    {
        var print = await CreateAsync(PrintStatus.Printing);
        var completion = new CompletePrintDto { Status = PrintStatus.Success, PrintTimeInSeconds = 100 };

        await CompleteAsync(print.Id, completion);
        await CompleteAsync(print.Id, completion);

        Assert.Single(await RowsAsync(print.Id), r => r.Type == PrintTimelineEventType.Finished);
    }

    [Fact]
    public async Task CompleteWhilePaused_ClosesThePauseAtTheEnd()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        var print = await CreateAsync(PrintStatus.Printing, startDate: start);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPrintTimeline>().ApplyAsync(print.Id,
                [new TimelineInput(TimelineInputType.Pause, start.AddMinutes(10), "p1")], PrintSource.ApiKey, Ct);
        }

        await CompleteAsync(print.Id, new CompletePrintDto { Status = PrintStatus.Cancelled, EndedAt = start.AddMinutes(13) });

        var stored = await ReadPrintAsync(print.Id);
        Assert.Null(stored.PausedSince);
        Assert.Equal(180, stored.PausedSeconds);
        var pause = Assert.Single(await RowsAsync(print.Id), r => r.Type == PrintTimelineEventType.Paused);
        Assert.Equal(start.AddMinutes(13), pause.EndedAt);
    }

    [Fact]
    public async Task Claim_RecordsClaimedOnce()
    {
        var print = await CreateAsync(PrintStatus.Printing);
        var claim = new ClaimPrintDto { ExternalSource = "moonraker", ExternalId = $"inst:{Guid.NewGuid():N}" };

        await SendAsync(HttpMethod.Post, $"/api/Prints/{print.Id}/claim", claim);
        await SendAsync(HttpMethod.Post, $"/api/Prints/{print.Id}/claim", claim);

        var claimed = Assert.Single(await RowsAsync(print.Id), r => r.Type == PrintTimelineEventType.Claimed);
        Assert.Equal("moonraker", claimed.Code);
    }

    [Fact]
    public async Task FinishingReplayOfACreate_RecordsFinished()
    {
        var externalId = $"inst:{Guid.NewGuid():N}";
        var print = await CreateAsync(PrintStatus.Printing, externalId: externalId);

        await CreateAsync(PrintStatus.Success, externalId: externalId);

        var rows = await RowsAsync(print.Id);
        Assert.Equal([PrintTimelineEventType.Started, PrintTimelineEventType.Finished], rows.Select(r => r.Type));
    }

    [Fact]
    public async Task PendingReplayedAsPrinting_RecordsStarted()
    {
        var externalId = $"inst:{Guid.NewGuid():N}";
        var print = await CreateAsync(PrintStatus.Pending, externalId: externalId);
        Assert.Empty(await RowsAsync(print.Id));

        await CreateAsync(PrintStatus.Printing, externalId: externalId);

        Assert.Single(await RowsAsync(print.Id), r => r.Type == PrintTimelineEventType.Started);
    }

    private async Task<PrintDetailDTO> CreateAsync(PrintStatus status, DateTimeOffset? startDate = null, string? externalId = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Prints", new AddPrintDTO
        {
            Title = $"lifecycle {Guid.NewGuid():N}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = status,
            ViewStatus = PrintViewStatus.Private,
            StartDate = startDate,
            ExternalSource = externalId is null ? null : "test-connector",
            ExternalId = externalId,
            FilamentUsage = [],
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!;
    }

    private async Task CompleteAsync(long id, CompletePrintDto body)
    {
        var response = await SendAsync(HttpMethod.Post, $"/api/Prints/{id}/complete", body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request, Ct);
    }

    private async Task NotifyAsync(PrintEventMessageDto message)
    {
        var outer = JsonSerializer.Serialize(new PrintEventDto { Message = JsonSerializer.Serialize(message) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Moonraker/notifier");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = new StringContent(outer, Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<long> PrintIdByFileAsync(string file)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Prints
            .Where(p => p.FileName == file).Select(p => p.Id).SingleAsync(Ct);
    }

    private async Task<Print> ReadPrintAsync(long printId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Prints
            .AsNoTracking().SingleAsync(p => p.Id == printId, Ct);
    }

    private async Task<List<PrintTimelineEvent>> RowsAsync(long printId)
    {
        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<PrintLogContext>().PrintTimelineEvents
            .AsNoTracking().Where(e => e.PrintId == printId).ToListAsync(Ct);
        return rows.OrderBy(r => r.Id).ToList();
    }
}
