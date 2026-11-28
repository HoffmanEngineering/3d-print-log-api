using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services.Timeline;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Timeline;

/// <summary>The pause and resume rules of <see cref="IPrintTimeline.ApplyAsync"/> (api#343).</summary>
public class PrintTimelinePauseTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pause_OpensASpanAndMarksThePrintPaused()
    {
        var (printId, start) = await AddPrintAsync();
        var at = start.AddMinutes(10);

        await ApplyAsync(printId, Pause(at, "p1"));

        var print = await ReadPrintAsync(printId);
        Assert.Equal(at, print.PausedSince);
        Assert.Null(print.PausedSeconds);
        var row = Assert.Single(await RowsAsync(printId));
        Assert.Equal(PrintTimelineEventType.Paused, row.Type);
        Assert.Equal(at, row.OccurredAt);
        Assert.Null(row.EndedAt);
        Assert.Equal(PrintSource.ApiKey, row.Source);
        Assert.Equal("p1", row.Key);
    }

    [Fact]
    public async Task Resume_ClosesTheSpanAndAddsItsSeconds()
    {
        var (printId, start) = await AddPrintAsync();

        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"), Resume(start.AddMinutes(16), "r1"));
        await ApplyAsync(printId, Pause(start.AddMinutes(20), "p2"), Resume(start.AddMinutes(21), "r2"));

        var print = await ReadPrintAsync(printId);
        Assert.Null(print.PausedSince);
        Assert.Equal(7 * 60, print.PausedSeconds);
        var rows = await RowsAsync(printId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(start.AddMinutes(16), rows[0].EndedAt);
        Assert.Equal(start.AddMinutes(21), rows[1].EndedAt);
    }

    [Fact]
    public async Task SecondPause_WhileOneIsOpen_IsIgnored()
    {
        var (printId, start) = await AddPrintAsync();

        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"), Pause(start.AddMinutes(11), "p2"));

        Assert.Equal(start.AddMinutes(10), (await ReadPrintAsync(printId)).PausedSince);
        Assert.Single(await RowsAsync(printId));
    }

    [Fact]
    public async Task Resume_WithNoOpenPause_IsIgnored()
    {
        var (printId, start) = await AddPrintAsync();

        await ApplyAsync(printId, Resume(start.AddMinutes(10), "r1"));

        Assert.Null((await ReadPrintAsync(printId)).PausedSeconds);
        Assert.Empty(await RowsAsync(printId));
    }

    [Fact]
    public async Task Resume_BeforeThePauseStarted_IsDropped()
    {
        var (printId, start) = await AddPrintAsync();

        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"));
        await ApplyAsync(printId, Resume(start.AddMinutes(9), "r1"));

        var print = await ReadPrintAsync(printId);
        Assert.Equal(start.AddMinutes(10), print.PausedSince);
        Assert.Null(Assert.Single(await RowsAsync(printId)).EndedAt);
    }

    [Fact]
    public async Task ReplayedPause_AfterItsResume_IsIgnored()
    {
        var (printId, start) = await AddPrintAsync();

        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"), Resume(start.AddMinutes(12), "r1"));
        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"), Resume(start.AddMinutes(12), "r1"));

        var print = await ReadPrintAsync(printId);
        Assert.Null(print.PausedSince);
        Assert.Equal(120, print.PausedSeconds);
        Assert.Single(await RowsAsync(printId));
    }

    [Fact]
    public async Task Pause_OnAPrintThatIsNotPrinting_IsIgnored()
    {
        var (printId, start) = await AddPrintAsync(PrintStatus.Success);

        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"));

        Assert.Null((await ReadPrintAsync(printId)).PausedSince);
        Assert.Empty(await RowsAsync(printId));
    }

    [Fact]
    public async Task Resume_AfterTheFinishClosedThePause_LeavesPausedSecondsAlone()
    {
        var (printId, start) = await AddPrintAsync();
        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"));
        await FinishAsync(printId, start.AddMinutes(15));

        await ApplyAsync(printId, Resume(start.AddMinutes(30), "r1"));

        var print = await ReadPrintAsync(printId);
        Assert.Null(print.PausedSince);
        Assert.Equal(5 * 60, print.PausedSeconds);
    }

    [Fact]
    public async Task Events_OutsideTheSkewWindow_AreDropped()
    {
        var (printId, start) = await AddPrintAsync();

        await ApplyAsync(printId,
            Pause(start.AddMinutes(-6), "early"),
            Pause(DateTimeOffset.UtcNow.AddMinutes(6), "late"));

        Assert.Null((await ReadPrintAsync(printId)).PausedSince);
        Assert.Empty(await RowsAsync(printId));
    }

    [Fact]
    public async Task Pause_JustInsideTheSkewWindow_IsKept()
    {
        var (printId, start) = await AddPrintAsync();

        await ApplyAsync(printId, Pause(start.AddMinutes(-4), "p1"));

        Assert.Equal(start.AddMinutes(-4), (await ReadPrintAsync(printId)).PausedSince);
    }

    [Fact]
    public async Task Events_PastTheCap_AreDroppedAndExistingRowsKept()
    {
        var (printId, start) = await AddPrintAsync();
        await SeedRowsAsync(printId, PrintTimelineEvent.MaxPerPrint);

        await ApplyAsync(printId, Pause(start.AddMinutes(10), "over"));

        Assert.Null((await ReadPrintAsync(printId)).PausedSince);
        Assert.Equal(PrintTimelineEvent.MaxPerPrint, (await RowsAsync(printId)).Count);
    }

    [Fact]
    public async Task Resume_PastTheCap_StillClosesTheOpenPause()
    {
        var (printId, start) = await AddPrintAsync();
        await ApplyAsync(printId, Pause(start.AddMinutes(10), "p1"));
        await SeedRowsAsync(printId, PrintTimelineEvent.MaxPerPrint - 1);

        await ApplyAsync(printId, Resume(start.AddMinutes(11), "r1"));

        Assert.Equal(60, (await ReadPrintAsync(printId)).PausedSeconds);
    }

    private static TimelineInput Pause(DateTimeOffset at, string key) => new(TimelineInputType.Pause, at, key);

    private static TimelineInput Resume(DateTimeOffset at, string key) => new(TimelineInputType.Resume, at, key);

    private async Task ApplyAsync(long printId, params TimelineInput[] inputs)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPrintTimeline>()
            .ApplyAsync(printId, inputs, PrintSource.ApiKey, Ct);
    }

    private async Task FinishAsync(long printId, DateTimeOffset at)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = await db.Prints.SingleAsync(p => p.Id == printId, Ct);
        print.Status = PrintStatus.Success;
        await PrintTimeline.StageFinishAsync(db, print, at, failure: null, Ct);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<(long Id, DateTimeOffset Start)> AddPrintAsync(PrintStatus status = PrintStatus.Printing)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        // Whole seconds, and far enough back that every offset the tests use is in the past.
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());
        var print = new Print
        {
            Title = "Timeline",
            FileName = $"timeline-{Guid.NewGuid():N}.gcode",
            StartDate = start,
            Status = status,
            ViewStatus = PrintViewStatus.Private,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId,
            FilamentUsage = [],
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return (print.Id, start);
    }

    private async Task SeedRowsAsync(long printId, int count)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        for (var i = 0; i < count; i++)
        {
            db.PrintTimelineEvents.Add(new PrintTimelineEvent
            {
                PrintId = printId,
                Type = PrintTimelineEventType.Claimed,
                OccurredAt = DateTimeOffset.UtcNow,
                Source = PrintSource.ApiKey,
                Key = $"seed-{i}",
            });
        }
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Print> ReadPrintAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Prints
            .AsNoTracking().SingleAsync(p => p.Id == printId, Ct);
    }

    private async Task<List<PrintTimelineEvent>> RowsAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<PrintLogContext>().PrintTimelineEvents
            .AsNoTracking().Where(e => e.PrintId == printId).ToListAsync(Ct);
        // SQLite cannot order by a DateTimeOffset.
        return rows.OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToList();
    }
}
