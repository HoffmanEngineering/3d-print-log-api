using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The shared print-event pipeline (#150). The notifier and the OctoPrint webhook are replayed
/// from recorded-shape bodies in tests-fixtures/print-events, so a change in how either handler
/// reads its payload shows up here, and a printer with a live bridge logs each job once.
/// </summary>
public class PrintEventPipelineTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public PrintEventPipelineTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    #region Moonraker notifier, recorded payloads

    [Fact]
    public async Task Notifier_RecordedStartedThenComplete_LogsOneSuccessfulPrint()
    {
        var printerId = await CreatePrinterAsync();
        var run = NewRun();

        Assert.Equal(HttpStatusCode.OK, (await PostNotifierAsync("moonraker-notifier-started.json", printerId, run)).StatusCode);
        var started = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(PrintStatus.Printing, started.Status);
        Assert.Equal(PrintSource.Moonraker, started.Source);
        Assert.Equal(printerId, started.PrinterId);
        Assert.Equal($"{run}_Benchy_0.2mm_PLA_MK3S_1h5m.gcode", started.FileName);
        Assert.Equal("moonraker-notifier", started.ExternalSource);
        Assert.StartsWith($"{printerId}:", started.ExternalId);
        Assert.EndsWith($":{run}_Benchy_0.2mm_PLA_MK3S_1h5m.gcode", started.ExternalId);

        Assert.Equal(HttpStatusCode.OK, (await PostNotifierAsync("moonraker-notifier-complete.json", printerId, run)).StatusCode);
        var done = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(started.Id, done.Id);
        Assert.Equal(PrintStatus.Success, done.Status);
        Assert.Equal(3912, done.PrintTimeInSeconds);
        var usage = Assert.Single(done.FilamentUsage!);
        Assert.Equal(4.21, usage.LengthInM);
        Assert.Equal(PrintFilament.SourceMeasurement.Length, usage.Source);
    }

    [Theory]
    [InlineData("moonraker-notifier-cancelled.json", 1388)]
    [InlineData("moonraker-notifier-error.json", 800)]
    public async Task Notifier_RecordedCancelledOrError_MarksThePrintFailed(string fixture, int printSeconds)
    {
        var printerId = await CreatePrinterAsync();
        var run = NewRun();
        await PostNotifierAsync("moonraker-notifier-started.json", printerId, run);

        Assert.Equal(HttpStatusCode.OK, (await PostNotifierAsync(fixture, printerId, run)).StatusCode);

        var print = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(PrintStatus.Failed, print.Status);
        Assert.Equal(printSeconds, print.PrintTimeInSeconds);
    }

    #endregion

    #region OctoPrint webhook, recorded payloads

    [Fact]
    public async Task OctoPrint_RecordedStartedThenDone_LogsOneSuccessfulPrint()
    {
        var printerId = await CreatePrinterAsync();
        var run = NewRun();
        var hash = NewHash();

        Assert.Equal(HttpStatusCode.OK, (await PostOctoPrintAsync("octoprint-print-started.json", printerId, run, hash)).StatusCode);
        var started = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(PrintStatus.Printing, started.Status);
        Assert.Equal(PrintSource.OctoPrint, started.Source);
        Assert.Equal(3813, started.EstimatedPrintTimeInSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1728560100), started.StartDate);
        Assert.Equal(4.21, Assert.Single(started.FilamentUsage!).EstimatedLengthInM);
        Assert.Equal("octoprint-webhook", started.ExternalSource);
        Assert.Equal($"{printerId}:{hash}:1728560100", started.ExternalId);

        Assert.Equal(HttpStatusCode.OK, (await PostOctoPrintAsync("octoprint-print-done.json", printerId, run, hash)).StatusCode);
        var done = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(started.Id, done.Id);
        Assert.Equal(PrintStatus.Success, done.Status);
        Assert.Equal(3901, done.PrintTimeInSeconds);
    }

    [Fact]
    public async Task OctoPrint_RecordedFailed_MarksThePrintFailed()
    {
        var printerId = await CreatePrinterAsync();
        var run = NewRun();
        var hash = NewHash();
        await PostOctoPrintAsync("octoprint-print-started.json", printerId, run, hash);

        Assert.Equal(HttpStatusCode.OK, (await PostOctoPrintAsync("octoprint-print-failed.json", printerId, run, hash)).StatusCode);

        var print = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(PrintStatus.Failed, print.Status);
        // 1200.5 rounds to even, as it always has.
        Assert.Equal(1200, print.PrintTimeInSeconds);
    }

    [Fact]
    public async Task OctoPrint_StartedDeliveredTwice_LogsOnePrintWithOneSnapshot()
    {
        var printerId = await CreatePrinterAsync();
        var run = NewRun();
        var hash = NewHash();

        await PostOctoPrintAsync("octoprint-print-started.json", printerId, run, hash, withSnapshot: true);
        var retry = await PostOctoPrintAsync("octoprint-print-started.json", printerId, run, hash, withSnapshot: true);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var print = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(1, await ImageCountAsync(print.Id));
    }

    #endregion

    #region The pipeline itself

    [Fact]
    public async Task Started_SameExternalIdTwice_ReturnsTheFirstPrint()
    {
        var printerId = await CreatePrinterAsync();
        var run = NewRun();
        var started = StartedEvent(printerId, run);

        using var scope = _factory.Services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IPrintEventService>();
        var first = await pipeline.Started(started);
        var second = await pipeline.Started(started);

        Assert.False(first.WasReplayed);
        Assert.True(second.WasReplayed);
        Assert.Equal(first.Print.Id, second.Print.Id);
        Assert.Single(await PrintsForRunAsync(run));
    }

    [Fact]
    public async Task Finished_WithNoPrintingMatch_ReturnsNullAndChangesNothing()
    {
        using var scope = _factory.Services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IPrintEventService>();

        var result = await pipeline.Finished(new PrintFinishedEvent
        {
            UserId = IntegrationTestSeeder.TestUserId,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FileName = $"{NewRun()}.gcode",
            Status = PrintStatus.Success,
            PrintTimeInSeconds = 10,
        });

        Assert.Null(result);
    }

    #endregion

    #region Notifier and bridge on one printer

    [Fact]
    public async Task NotifierAndBridgeBothActive_RecordOnePrintPerJob()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId);
        var run = NewRun();

        // The bridge logs the job…
        var bridgePrint = await PostBridgePrintAsync(printerId, instanceId, run);
        // …and the notifier still configured on the same Moonraker reports it too.
        Assert.Equal(HttpStatusCode.OK, (await PostNotifierAsync("moonraker-notifier-started.json", printerId, run)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostNotifierAsync("moonraker-notifier-complete.json", printerId, run)).StatusCode);

        var print = Assert.Single(await PrintsForRunAsync(run));
        Assert.Equal(bridgePrint.Id, print.Id);
        Assert.Equal("moonraker", print.ExternalSource);
        Assert.Equal(PrintStatus.Printing, print.Status);
    }

    [Fact]
    public async Task NotifierDroppedForABridge_IsCountedAndRaisesTheNotice()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId);
        var before = DateTimeOffset.UtcNow;

        await PostNotifierAsync("moonraker-notifier-started.json", printerId, NewRun());
        await PostNotifierAsync("moonraker-notifier-complete.json", printerId, NewRun());

        var connection = await GetConnectionAsync(instanceId);
        Assert.Equal(2, connection.DroppedNotifierEventCount);
        Assert.True(connection.LastDroppedNotifierEventAt >= before.AddSeconds(-1));
        Assert.True(connection.ShowNotifierNotice);
    }

    [Fact]
    public async Task NotifierUnhandledEvent_IsNotCountedAsDropped()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId);

        var body = System.IO.File.ReadAllText(FixturePath("moonraker-notifier-started.json")).Replace("\\\"started\\\"", "\\\"paused\\\"");
        await PostRawNotifierAsync(body, printerId, NewRun());

        Assert.Equal(0, (await GetConnectionAsync(instanceId)).DroppedNotifierEventCount);
    }

    [Fact]
    public async Task NotifierWithAStaleBridge_StillLogsThePrint()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId);
        await BackdateLastSeenAsync(instanceId, TimeSpan.FromMinutes(20));
        var run = NewRun();

        await PostNotifierAsync("moonraker-notifier-started.json", printerId, run);

        Assert.Equal("moonraker-notifier", Assert.Single(await PrintsForRunAsync(run)).ExternalSource);
        Assert.Equal(0, (await GetConnectionAsync(instanceId)).DroppedNotifierEventCount);
    }

    [Fact]
    public async Task NotifierWithABridgeOnAnotherPrinter_StillLogsThePrint()
    {
        var bridgedPrinter = await CreatePrinterAsync();
        await RegisterBridgeAsync(bridgedPrinter);
        var notifierPrinter = await CreatePrinterAsync();
        var run = NewRun();

        await PostNotifierAsync("moonraker-notifier-started.json", notifierPrinter, run);

        Assert.Equal(notifierPrinter, Assert.Single(await PrintsForRunAsync(run)).PrinterId);
    }

    [Fact]
    public async Task NotifierWithAnOctoPrintConnection_StillLogsThePrint()
    {
        var printerId = await CreatePrinterAsync();
        await RegisterBridgeAsync(printerId, kind: "octoprint");
        var run = NewRun();

        await PostNotifierAsync("moonraker-notifier-started.json", printerId, run);

        Assert.Single(await PrintsForRunAsync(run));
    }

    [Fact]
    public async Task DismissNotifierNotice_HidesItForGood_AndKeepsTheCount()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId);
        await PostNotifierAsync("moonraker-notifier-started.json", printerId, NewRun());

        var response = await SendAsync(HttpMethod.Post, $"/api/Connections/{instanceId}/notifier-notice/dismiss");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await PostNotifierAsync("moonraker-notifier-started.json", printerId, NewRun());
        var connection = await GetConnectionAsync(instanceId);
        Assert.False(connection.ShowNotifierNotice);
        Assert.NotNull(connection.NotifierNoticeDismissedAt);
        Assert.Equal(2, connection.DroppedNotifierEventCount);
    }

    [Fact]
    public async Task DismissNotifierNotice_UnknownConnection_IsNotFound()
    {
        var response = await SendAsync(HttpMethod.Post, $"/api/Connections/{Guid.NewGuid()}/notifier-notice/dismiss");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_KeepsTheDroppedCount()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId);
        await PostNotifierAsync("moonraker-notifier-started.json", printerId, NewRun());

        await RegisterBridgeAsync(printerId, instanceId: instanceId);

        Assert.Equal(1, (await GetConnectionAsync(instanceId)).DroppedNotifierEventCount);
    }

    [Fact]
    public async Task NewConnection_HasNoNotice()
    {
        var instanceId = await RegisterBridgeAsync(await CreatePrinterAsync());

        var connection = await GetConnectionAsync(instanceId);

        Assert.Equal(0, connection.DroppedNotifierEventCount);
        Assert.Null(connection.LastDroppedNotifierEventAt);
        Assert.False(connection.ShowNotifierNotice);
    }

    #endregion

    #region Helpers

    private static string NewRun() => $"run{Guid.NewGuid():N}";

    private static string NewHash() => Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + "00000000";

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "print-events", name);

    private static PrintStartedEvent StartedEvent(long printerId, string run) => new()
    {
        UserId = IntegrationTestSeeder.TestUserId,
        PrinterId = printerId,
        Source = PrintSource.Moonraker,
        ExternalSource = "moonraker-notifier",
        ExternalId = $"{printerId}:1728560100:{run}.gcode",
        Title = run,
        FileName = $"{run}.gcode",
        StartDate = DateTimeOffset.FromUnixTimeSeconds(1728560100),
        Usage = [],
    };

    private Task<HttpResponseMessage> PostNotifierAsync(string fixture, long printerId, string run)
        => PostRawNotifierAsync(System.IO.File.ReadAllText(FixturePath(fixture)), printerId, run);

    private async Task<HttpResponseMessage> PostRawNotifierAsync(string body, long printerId, string run)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Moonraker/notifier");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = new StringContent(
            body.Replace("__PRINTER_ID__", printerId.ToString()).Replace("__RUN__", run),
            Encoding.UTF8,
            "application/json");
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sends an OctoPrint fixture as the plugin does when snapshots are on: multipart form fields,
    /// objects as JSON text.
    /// </summary>
    private async Task<HttpResponseMessage> PostOctoPrintAsync(string fixture, long printerId, string run, string hash, bool withSnapshot = false)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(FixturePath(fixture)))!;
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in fields)
        {
            content.Add(new StringContent(value.Replace("__PRINTER_ID__", printerId.ToString()).Replace("__RUN__", run).Replace("__HASH__", hash)), name);
        }
        if (withSnapshot)
        {
            var image = new ByteArrayContent(System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "exif-orientation-6.jpg")));
            image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(image, "snapshot", "snapshot.jpg");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Octoprint") { Content = content };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<string> RegisterBridgeAsync(long printerId, string kind = "moonraker", string? instanceId = null)
    {
        instanceId ??= Guid.NewGuid().ToString();
        var response = await SendAsync(HttpMethod.Put, $"/api/Connections/{instanceId}", new PutConnectionDto
        {
            Kind = kind,
            DisplayName = "Pipeline bridge",
            AgentVersion = "0.1.0",
            PrinterId = printerId,
        });
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");
        return instanceId;
    }

    private async Task<ConnectionDto> GetConnectionAsync(string instanceId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/Connections/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConnectionDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> PostBridgePrintAsync(long printerId, string instanceId, string run)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Prints", new AddPrintDTO
        {
            Title = "Benchy",
            PrinterId = printerId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Private,
            FileName = $"{run}_Benchy_0.2mm_PLA_MK3S_1h5m.gcode",
            ExternalSource = "moonraker",
            ExternalId = $"{instanceId}:000042",
            ConnectionInstanceId = instanceId,
            FilamentUsage = [],
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Every print of the test user whose file name carries this run's marker.</summary>
    private async Task<List<Print>> PrintsForRunAsync(string run)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints
            .Include(p => p.FilamentUsage)
            .Where(p => p.CreatedById == IntegrationTestSeeder.TestUserId && p.FileName!.Contains(run))
            .AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> ImageCountAsync(long printId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrintImages.CountAsync(i => i.PrintId == printId, TestContext.Current.CancellationToken);
    }

    private async Task BackdateLastSeenAsync(string instanceId, TimeSpan by)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connection = await db.Connections.SingleAsync(
            c => c.InstanceId == instanceId && c.UserId == IntegrationTestSeeder.TestUserId,
            TestContext.Current.CancellationToken);
        connection.LastSeenAt = DateTime.UtcNow - by;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A fresh printer per test, so one test's bridge never suppresses another's notifier.</summary>
    private async Task<long> CreatePrinterAsync()
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "/api/Printers",
            new AddPrinterDTO { Name = $"Pipeline printer {Guid.NewGuid():N}", Make = "Make", Model = "Model", IsActive = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }

    #endregion
}
