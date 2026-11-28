using System.Text.Json;
using PrintLogApi.IntegrationTests.Sync;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Analytics;
using PrintLogApi.Services.Analytics;
using PrintLogApi.Services.Timeline;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Timeline;

/// <summary>The sync snapshot's <c>isPaused</c> and accuracy on active time (api#343).</summary>
public class PausedSyncAndAccuracyTests(SyncTestFactory factory) : IClassFixture<SyncTestFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Snapshot_CurrentPrintIsPausedWhilePaused_AndTheVersionMoves()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user);
        var now = factory.Clock.GetUtcNow();
        var printId = await AddPrintAsync(user, printer, PrintStatus.Printing, now.AddHours(-1), estimated: null, actual: null);

        Assert.False((await CurrentPrintAsync(user)).GetProperty("isPaused").GetBoolean());
        var before = await VersionAsync(user);

        await ApplyAsync(printId, new TimelineInput(TimelineInputType.Pause, now.AddMinutes(-10), "p1"));

        Assert.NotEqual(before, await VersionAsync(user));
        Assert.True((await CurrentPrintAsync(user)).GetProperty("isPaused").GetBoolean());
    }

    [Fact]
    public async Task Accuracy_ComparesTheEstimateWithActiveTime_WhenThePrintPaused()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user);
        var start = factory.Clock.GetUtcNow().AddDays(-2);
        for (var i = 0; i < AccuracyStats.MinSampleSize; i++)
        {
            var id = await AddPrintAsync(user, printer, PrintStatus.Success, start.AddHours(i * 3), estimated: 3600, actual: 4200);
            await SetPausedSecondsAsync(id, 600);
        }

        using var scope = factory.Services.CreateScope();
        var filter = new AnalyticsFilter { TimeZone = "UTC", FromDate = start.AddDays(-1), ToDate = factory.Clock.GetUtcNow() };
        filter.Normalize(factory.Clock.GetUtcNow());
        var accuracy = await scope.ServiceProvider.GetRequiredService<IAccuracyAnalyticsService>().GetAccuracy(user.Id, filter, Ct);

        var group = Assert.Single(accuracy.ByPrinter);
        Assert.Equal(1.0, group.MedianRatio!.Value, 3);
    }

    private async Task<JsonElement> CurrentPrintAsync(User user)
    {
        var response = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("printers")[0].GetProperty("currentPrint").Clone();
    }

    private async Task<string> VersionAsync(User user)
    {
        var response = await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private async Task ApplyAsync(long printId, TimelineInput input)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPrintTimeline>().ApplyAsync(printId, [input], PrintSource.ApiKey, Ct);
    }

    private async Task<Printer> AddPrinterAsync(User user)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer { Name = "Paused", UserId = user.Id, IsActive = true, CategoryNickname = "FDM", SlotCount = 1, Make = "LDO", Model = "Voron 2.4" };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(Ct);
        return printer;
    }

    private async Task<long> AddPrintAsync(User user, Printer printer, PrintStatus status, DateTimeOffset start, int? estimated, int? actual)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = "Paused print",
            Status = status,
            StartDate = start,
            PrinterId = printer.Id,
            CreatedById = user.Id,
            UpdatedById = user.Id,
            ViewStatus = PrintViewStatus.Private,
            EstimatedPrintTimeInSeconds = estimated,
            PrintTimeInSeconds = actual,
            FilamentUsage = [],
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print.Id;
    }

    private async Task SetPausedSecondsAsync(long printId, int seconds)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = await db.Prints.FindAsync([printId], Ct);
        print!.PausedSeconds = seconds;
        await db.SaveChangesAsync(Ct);
    }
}
