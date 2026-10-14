using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Controllers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.Services;
using PrintLogApi.Services.Spoolman;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// <c>PUT /spoolman/api/v1/spool/{id}/use</c> stored (#171): Moonraker's reports accumulate per
/// (print, spool) against the printer's open notifier print, and nowhere else.
/// </summary>
public class SpoolmanUsageRecordingTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const string Base = "/spoolman/api/v1";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Use_DuringANotifierPrint_IsAccumulated()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var spool = await AddSpoolAsync(user, "PLA");
        var print = await AddPrintAsync(user, printer, MoonrakerController.NotifierSource);

        Assert.Equal(HttpStatusCode.OK, (await UseAsync(secret, spool, 100)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UseAsync(secret, spool, 50)).StatusCode);

        var row = Assert.Single(await UsagesAsync(print));
        Assert.Equal(spool.Id, row.FilamentId);
        Assert.Equal(150, row.LengthMm, 6);
    }

    [Fact]
    public async Task Use_Negative_Subtracts()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var spool = await AddSpoolAsync(user, "PLA");
        var print = await AddPrintAsync(user, printer, MoonrakerController.NotifierSource);

        await UseAsync(secret, spool, 100);
        await UseAsync(secret, spool, -30);

        Assert.Equal(70, Assert.Single(await UsagesAsync(print)).LengthMm, 6);
    }

    [Fact]
    public async Task Use_WithoutAnOpenPrint_IsNotStored()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var spool = await AddSpoolAsync(user, "PLA");
        // A finished notifier print is not open.
        var finished = await AddPrintAsync(user, printer, MoonrakerController.NotifierSource, PrintStatus.Success);

        var response = await UseAsync(secret, spool, 100);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(spool.SpoolNumber, body.RootElement.GetProperty("id").GetInt32());
        Assert.Empty(await UsagesAsync(finished));
        Assert.Empty(await UsagesForUserAsync(user));
    }

    [Fact]
    public async Task Use_WithALiveBridge_IsNotStored()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var spool = await AddSpoolAsync(user, "PLA");
        var print = await AddPrintAsync(user, printer, MoonrakerController.NotifierSource);
        var bridge = await AddBridgeAsync(user, printer);

        Assert.Equal(HttpStatusCode.OK, (await UseAsync(secret, spool, 100)).StatusCode);

        Assert.Empty(await UsagesAsync(print));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connection = await db.Connections.AsNoTracking().SingleAsync(c => c.Id == bridge, Ct);
        // Checking for the bridge is not dropping a notifier event.
        Assert.Equal(0, connection.DroppedNotifierEventCount);
        Assert.Null(connection.LastDroppedNotifierEventAt);
    }

    [Fact]
    public async Task Use_OnABridgePrint_IsNotStored()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var spool = await AddSpoolAsync(user, "PLA");
        var print = await AddPrintAsync(user, printer, "moonraker");

        Assert.Equal(HttpStatusCode.OK, (await UseAsync(secret, spool, 100)).StatusCode);

        Assert.Empty(await UsagesAsync(print));
    }

    [Fact]
    public async Task Use_OnASingleSlotPrinter_LoadsTheSpool()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var a = await AddSpoolAsync(user, "A");
        var b = await AddSpoolAsync(user, "B");
        await LoadAsync(printer, a, slot: 0);
        await AddPrintAsync(user, printer, MoonrakerController.NotifierSource);

        await UseAsync(secret, b, 10);

        var loaded = Assert.Single(await LoadedAsync(printer));
        Assert.Equal(b.Id, loaded.FilamentId);
        Assert.Equal(0, loaded.Slot);
    }

    [Fact]
    public async Task Use_OnAMultiSlotPrinter_LeavesSlotsAlone()
    {
        var (user, secret, printer) = await ConnectedAsync();
        await SetSlotCountAsync(printer, 4);
        var a = await AddSpoolAsync(user, "A");
        var b = await AddSpoolAsync(user, "B");
        await LoadAsync(printer, a, slot: 0);
        var print = await AddPrintAsync(user, printer, MoonrakerController.NotifierSource);

        await UseAsync(secret, b, 10);

        // Recorded against the print, but which slot B is in is not this report's to say.
        Assert.Equal(b.Id, Assert.Single(await UsagesAsync(print)).FilamentId);
        var loaded = Assert.Single(await LoadedAsync(printer));
        Assert.Equal(a.Id, loaded.FilamentId);
        Assert.Equal(0, loaded.Slot);
    }

    /// <summary>
    /// The race the unique index exists for, forced deterministically: the shared in-memory
    /// SQLite connection only interleaves requests, so it would never lose it on its own. The
    /// second report's insert lands between the first report's empty increment and its insert;
    /// the first then hits the unique index and retries as an increment.
    /// </summary>
    [Fact]
    public async Task Use_ConcurrentFirstReports_SumIntoOneRow()
    {
        var (user, _, printer) = await ConnectedAsync();
        var spool = await AddSpoolAsync(user, "PLA");
        // Already loaded, so neither report has a slot to write alongside its insert.
        await LoadAsync(printer, spool, slot: 0);
        var print = await AddPrintAsync(user, printer, MoonrakerController.NotifierSource);

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<PrintLogContext>>();
        var race = new InsertFirstInterceptor(async () =>
        {
            using var other = factory.Services.CreateScope();
            var outcome = await other.ServiceProvider.GetRequiredService<ISpoolmanUsageRecorder>()
                .RecordAsync(user.Id, printer, spool.Id, 50, Ct);
            Assert.Equal(SpoolmanUseOutcome.Recorded, outcome);
        });
        using var db = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>(options).AddInterceptors(race).Options);
        var recorder = ActivatorUtilities.CreateInstance<SpoolmanUsageRecorder>(
            scope.ServiceProvider,
            db,
            ActivatorUtilities.CreateInstance<PrinterService>(scope.ServiceProvider, db));

        var first = await recorder.RecordAsync(user.Id, printer, spool.Id, 100, Ct);

        Assert.Equal(SpoolmanUseOutcome.Recorded, first);
        Assert.True(race.Fired);
        Assert.Equal(150, Assert.Single(await UsagesAsync(print)).LengthMm, 6);
    }

    [Fact]
    public async Task Complete_WithAccumulations_ReplacesTheTotal()
    {
        var (user, secret, printer, a, b, file) = await StartedWithTwoSpoolsAsync();
        await UseAsync(secret, a, 1000);
        await UseAsync(secret, b, 500);

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(secret, "complete", printer, file, filamentUsed: 9999)).StatusCode);

        var print = Assert.Single(await PrintsAsync(user, file));
        Assert.Equal(PrintStatus.Success, print.Status);
        var rows = (await FilamentUsageAsync(print.Id)).OrderBy(r => r.LengthInM).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(b.Id, rows[0].FilamentId);
        Assert.Equal(0.5, rows[0].LengthInM);
        Assert.Equal(a.Id, rows[1].FilamentId);
        Assert.Equal(1.0, rows[1].LengthInM);
        Assert.All(rows, r =>
        {
            Assert.Equal(PrintFilament.SourceMeasurement.Length, r.Source);
            Assert.Equal(PrintFilament.SourceMeasurement.Length, r.EstimatedSource);
            Assert.Equal(r.LengthInM, r.EstimatedLengthInM);
            Assert.NotNull(r.AmountMg);
        });
        Assert.Empty(await UsagesAsync(print.Id));
    }

    [Fact]
    public async Task Cancelled_WithAccumulations_AlsoReplaces()
    {
        var (user, secret, printer, a, b, file) = await StartedWithTwoSpoolsAsync();
        await UseAsync(secret, a, 1000);
        await UseAsync(secret, b, 500);

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(secret, "cancelled", printer, file, filamentUsed: 9999)).StatusCode);

        var print = Assert.Single(await PrintsAsync(user, file));
        Assert.Equal(PrintStatus.Failed, print.Status);
        var rows = (await FilamentUsageAsync(print.Id)).OrderBy(r => r.LengthInM).ToList();
        Assert.Equal([0.5, 1.0], rows.Select(r => r.LengthInM));
        Assert.Equal([b.Id, a.Id], rows.Select(r => r.FilamentId));
        Assert.Empty(await UsagesAsync(print.Id));
    }

    [Fact]
    public async Task Complete_NegativeTotal_RecordsZero()
    {
        var (user, secret, printer, a, _, file) = await StartedWithTwoSpoolsAsync();
        await UseAsync(secret, a, 100);
        await UseAsync(secret, a, -300);

        await NotifyAsync(secret, "complete", printer, file, filamentUsed: 9999);

        var print = Assert.Single(await PrintsAsync(user, file));
        var row = Assert.Single(await FilamentUsageAsync(print.Id));
        Assert.Equal(a.Id, row.FilamentId);
        Assert.Equal(0, row.LengthInM);
    }

    [Fact]
    public async Task Complete_DeletedSpool_IsSkipped()
    {
        var (user, secret, printer, a, b, file) = await StartedWithTwoSpoolsAsync();
        await UseAsync(secret, a, 1000);
        await UseAsync(secret, b, 500);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IFilamentService>().DeleteFilament(b.Id);
        }

        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(secret, "complete", printer, file, filamentUsed: 9999)).StatusCode);

        var print = Assert.Single(await PrintsAsync(user, file));
        Assert.Equal(PrintStatus.Success, print.Status);
        var row = Assert.Single(await FilamentUsageAsync(print.Id));
        Assert.Equal(a.Id, row.FilamentId);
        Assert.Equal(1.0, row.LengthInM);
        Assert.Empty(await UsagesAsync(print.Id));
    }

    [Fact]
    public async Task Complete_WithoutAccumulations_Unchanged()
    {
        var (user, secret, printer, a, _, file) = await StartedWithTwoSpoolsAsync();

        await NotifyAsync(secret, "complete", printer, file, filamentUsed: 2500);

        var print = Assert.Single(await PrintsAsync(user, file));
        var row = Assert.Single(await FilamentUsageAsync(print.Id));
        Assert.Equal(a.Id, row.FilamentId);
        Assert.Equal(2.5, row.LengthInM);
    }

    /// <summary>
    /// A single-slot printer with spool A loaded, and a notifier print started through the real
    /// route (so it carries the slot-0 row on A). Spool B is the user's but not loaded.
    /// </summary>
    private async Task<(User User, string Secret, long Printer, Filament A, Filament B, string File)> StartedWithTwoSpoolsAsync()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var a = await AddSpoolAsync(user, "A");
        var b = await AddSpoolAsync(user, "B");
        await LoadAsync(printer, a, slot: 0);
        var file = $"usage-{Guid.NewGuid():N}.gcode";
        Assert.Equal(HttpStatusCode.OK, (await NotifyAsync(secret, "started", printer, file)).StatusCode);
        return (user, secret, printer, a, b, file);
    }

    private Task<HttpResponseMessage> NotifyAsync(string secret, string name, long printer, string file, double filamentUsed = 0)
    {
        var inner = JsonSerializer.Serialize(new PrintEventMessageDto
        {
            EventName = name,
            Filename = file,
            PrinterId = printer,
            FilamentUsed = filamentUsed,
            TotalDuration = 120,
            PrintDuration = 120,
        });
        var outer = JsonSerializer.Serialize(new PrintEventDto { Message = inner }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, "/spoolman/notifier")
        {
            Content = new StringContent(outer, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = SpoolmanTestSupport.Basic(secret);
        return _client.SendAsync(request, Ct);
    }

    private async Task<List<Print>> PrintsAsync(User user, string file)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints.AsNoTracking().Where(p => p.CreatedById == user.Id && p.FileName == file).ToListAsync(Ct);
    }

    private async Task<List<PrintFilament>> FilamentUsageAsync(long print)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrintFilament.AsNoTracking().Where(pf => pf.PrintId == print).ToListAsync(Ct);
    }

    /// <summary>Runs <paramref name="before"/> once, just before the first insert into SpoolmanUsages.</summary>
    private sealed class InsertFirstInterceptor(Func<Task> before) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("INSERT INTO \"SpoolmanUsages\"", StringComparison.Ordinal))
            {
                Fired = true;
                await before();
            }
            return result;
        }
    }

    #region Helpers

    private async Task<(User User, string Secret, long Printer)> ConnectedAsync()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printer);
        return (user, connection.Secret!, printer);
    }

    private async Task<Filament> AddSpoolAsync(User user, string name)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFilamentService>().AddFilament(new AddFilamentDto
        {
            DisplayName = name,
            Brand = "Generic",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            ColorHex = "112233",
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            MaterialDensityGramPerCubicCm = 1.24,
            IsActive = true,
        }, user.Id);
    }

    private async Task<long> AddPrintAsync(User user, long printer, string externalSource, PrintStatus status = PrintStatus.Printing)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = "Recording",
            StartDate = DateTimeOffset.UtcNow,
            Status = status,
            ViewStatus = PrintViewStatus.Private,
            PrinterId = printer,
            CreatedById = user.Id,
            UpdatedById = user.Id,
            ExternalSource = externalSource,
            ExternalId = Guid.NewGuid().ToString("N"),
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print.Id;
    }

    private async Task<Guid> AddBridgeAsync(User user, long printer)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connection = new Connection
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Kind = Connection.MoonrakerKind,
            InstanceId = Guid.NewGuid().ToString("N"),
            DisplayName = "Bridge",
            PrinterId = printer,
            CreatedDate = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        };
        db.Connections.Add(connection);
        await db.SaveChangesAsync(Ct);
        return connection.Id;
    }

    private async Task LoadAsync(long printer, Filament spool, int slot)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.PrinterFilament.Add(new PrinterFilament
        {
            PrinterId = printer,
            FilamentId = spool.Id,
            Slot = slot,
            LoadedDateTime = DateTimeOffset.Now,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetSlotCountAsync(long printer, int slots)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        await db.Printers.Where(p => p.Id == printer).ExecuteUpdateAsync(s => s.SetProperty(p => p.SlotCount, slots), Ct);
    }

    private async Task<List<PrinterFilament>> LoadedAsync(long printer)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrinterFilament.AsNoTracking().Where(pf => pf.PrinterId == printer).ToListAsync(Ct);
    }

    private async Task<List<SpoolmanUsage>> UsagesAsync(long print)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.SpoolmanUsages.AsNoTracking().Where(u => u.PrintId == print).ToListAsync(Ct);
    }

    private async Task<List<SpoolmanUsage>> UsagesForUserAsync(User user)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var prints = db.Prints.Where(p => p.CreatedById == user.Id).Select(p => p.Id);
        return await db.SpoolmanUsages.AsNoTracking().Where(u => prints.Contains(u.PrintId)).ToListAsync(Ct);
    }

    private Task<HttpResponseMessage> UseAsync(string secret, Filament spool, double mm)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Base}/spool/{spool.SpoolNumber}/use")
        {
            Content = new StringContent(
                $$"""{"use_length": {{mm.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = SpoolmanTestSupport.Basic(secret);
        return _client.SendAsync(request, Ct);
    }

    #endregion
}
