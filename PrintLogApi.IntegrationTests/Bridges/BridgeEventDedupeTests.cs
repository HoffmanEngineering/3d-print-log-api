using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// The cross-bridge safety net (printlog-bridge#103, story 15a). A duplicate print is acceptable; a
/// lost print, or two jobs merged into one, is not. So nothing is ever refused: a bridge's job for
/// a printer another of the user's bridges is bound to gets the bound bridge's print when that
/// bridge already logged the same job (same file, starts within the kind's tolerance), and is
/// logged otherwise. Either way the job is counted on the bridge once, for the "two bridges watch
/// this printer" warning. After a move, a job running at the move stays one print when the start
/// times allow: the new bridge picking it up is handed the old bridge's print, and the old
/// bridge's late create gets the print the new one logged.
/// </summary>
public class BridgeEventDedupeTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    private sealed record Setup(PairedBridge A, PairedBridge B, long PrinterId, long EntryId);

    /// <summary>Two bridges; the printer is connected to <paramref name="boundTo"/> ("a" or "b").</summary>
    private async Task<Setup> SetupAsync(string label, string boundTo = "b", string kind = "moonraker")
    {
        var a = await PairBridgeAsync(_client, label);
        var paired = await PairAsync(_client, a.Subject, AllBridgeScopes, name: "printlog-bridge on workshop");
        var b = new PairedBridge(a.Subject, paired.GetProperty("token").GetString()!, paired.GetProperty("id").GetInt64());
        await CheckInOkAsync(_client, a.Token, CheckInBody());
        await CheckInOkAsync(_client, b.Token, new { networks = new[] { "192.168.2.0/24" } });
        var printerId = await CreatePrinterAsync(_client, a.Subject);
        var bound = boundTo == "a" ? a : b;
        var connected = await Send(UserJson(HttpMethod.Post, $"/api/bridges/{bound.GrantId}/printers", a.Subject, new
        {
            kind,
            host = boundTo == "a" ? "192.168.1.30" : "192.168.2.30",
            serial = kind == "bambu" ? "DD" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant() : null,
            printerId,
        }));
        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);
        var entryId = (await Json(connected)).GetProperty("id").GetInt64();

        // Bound two hours ago, so the jobs these tests start half an hour back are the bound
        // bridge's era, not jobs from before the printer was bound.
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PrintLogContext>().BridgePrinters.Where(p => p.Id == entryId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.CreatedDate, DateTime.UtcNow.AddHours(-2)), TestContext.Current.CancellationToken);
        return new Setup(a, b, printerId, entryId);
    }

    /// <summary>The bridge's heartbeat for the printer: its watcher is running.</summary>
    private async Task<string> HeartbeatAsync(PairedBridge bridge, long printerId)
    {
        var instanceId = Guid.NewGuid().ToString();
        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { kind = "moonraker", displayName = "voron", printerId }),
        }, bridge.Token));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return instanceId;
    }

    private static HttpRequestMessage Create(PairedBridge bridge, long printerId, string instanceId, string jobId, DateTimeOffset? startDate, string? fileName = "cube.gcode", string? connectionInstanceId = null, int? printTimeInSeconds = null)
        => WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new
            {
                title = "cube",
                printerId,
                fileName,
                startDate,
                printTimeInSeconds,
                status = PrintStatus.Printing,
                externalSource = "printlog-bridge",
                externalId = $"{instanceId}:{jobId}",
                connectionInstanceId = connectionInstanceId ?? instanceId,
            }),
        }, bridge.Token);

    private static HttpRequestMessage Claim(PairedBridge bridge, long printId, string instanceId, string jobId, DateTimeOffset? startDate = null)
        => WithDevice(new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/claim")
        {
            Content = JsonContent.Create(new { externalSource = "printlog-bridge", externalId = $"{instanceId}:{jobId}", connectionInstanceId = instanceId, startDate = startDate ?? DateTimeOffset.UtcNow }),
        }, bridge.Token);

    private static HttpRequestMessage Complete(PairedBridge bridge, long printId, object body)
        => WithDevice(new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/complete") { Content = JsonContent.Create(body) }, bridge.Token);

    private async Task<long> UploadedAsync(Setup s, DateTimeOffset? startDate = null)
    {
        var uploaded = await Send(UserJson(HttpMethod.Post, "/api/Prints", s.A.Subject, new { title = "uploaded", printerId = s.PrinterId, fileName = "cube.gcode", startDate, status = PrintStatus.Printing }));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        return (await Json(uploaded)).GetProperty("id").GetInt64();
    }

    private Task<HttpResponseMessage> Move(Setup s, PairedBridge from, PairedBridge to)
        => Send(UserJson(HttpMethod.Post, $"/api/bridges/{from.GrantId}/printers/{s.EntryId}/move", from.Subject, new { toBridgeId = to.GrantId, host = "192.168.2.30" }));

    private async Task<DateTimeOffset> MovedAtAsync(long entryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var moved = await db.BridgePrinters.Where(p => p.Id == entryId).Select(p => p.MovedAt).SingleAsync(TestContext.Current.CancellationToken);
        return new DateTimeOffset(DateTime.SpecifyKind(moved!.Value, DateTimeKind.Utc));
    }

    /// <summary>Moves the printer from A to B and returns when, for tests that place starts around the move.</summary>
    private async Task<DateTimeOffset> MoveToBAsync(Setup s)
    {
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        return await MovedAtAsync(s.EntryId);
    }

    private async Task<int> PrintCountAsync(long printerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints.CountAsync(p => p.PrinterId == printerId, TestContext.Current.CancellationToken);
    }

    private Task<JsonElement> BridgeAsync(Setup s, PairedBridge bridge) => GetAsUserAsync(_client, s.A.Subject, $"/api/bridges/{bridge.GrantId}");

    private async Task<int> SharedAsync(Setup s, PairedBridge bridge) => (await BridgeAsync(s, bridge)).GetProperty("sharedJobCount").GetInt32();

    private static async Task<long> IdOf(HttpResponseMessage response) => (await Json(response)).GetProperty("id").GetInt64();

    // ---- Two bridges watching one printer ----

    /// <summary>
    /// The blocker the evidence rule couldn't fix: an online bound bridge whose adapter can't reach
    /// the printer still heartbeats with no error. Nothing is refused, whatever its state.
    /// </summary>
    [Fact]
    public async Task AJob_FromABridgeThePrinterIsNotBoundTo_IsLogged_AndCounted()
    {
        var s = await SetupAsync("shared-logged");
        await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);

        var response = await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
        var a = await BridgeAsync(s, s.A);
        Assert.Equal(1, a.GetProperty("sharedJobCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, a.GetProperty("lastSharedJobAt").ValueKind);
        Assert.Equal(s.PrinterId, a.GetProperty("lastSharedJobPrinterId").GetInt64());
        Assert.Equal(0, await SharedAsync(s, s.B));
    }

    [Fact]
    public async Task AJob_TheBoundBridgeAlreadyLogged_IsAnsweredWithThatPrint()
    {
        var s = await SetupAsync("shared-same-job");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var bPrint = await Send(Create(s.B, s.PrinterId, bInstance, "41", started));
        Assert.Equal(HttpStatusCode.Created, bPrint.StatusCode);

        var aPrint = await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddSeconds(30)));

        Assert.Equal(HttpStatusCode.OK, aPrint.StatusCode);
        var print = await Json(aPrint);
        Assert.Equal(await IdOf(bPrint), print.GetProperty("id").GetInt64());
        Assert.Equal($"{bInstance}:41", print.GetProperty("externalId").GetString());
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
        Assert.Equal(1, await SharedAsync(s, s.A));
    }

    /// <summary>The bridge sends a job at its start, again at its end, and may claim it: one job, counted once.</summary>
    [Fact]
    public async Task TheSameJob_IsCountedOnce()
    {
        var s = await SetupAsync("shared-count-once");
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var uploadedId = await UploadedAsync(s);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
        await Send(Claim(s.A, uploadedId, instance, "1"));
        Assert.Equal(1, await SharedAsync(s, s.A));

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "2", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(2, await SharedAsync(s, s.A));
    }

    /// <summary>
    /// Both bridges read the start from the printer: Moonraker's job history and PrusaLink's
    /// printing time agree to seconds, so 2 minutes. A Bambu Lab printer without
    /// <c>gcode_start_time</c> gives each bridge its own first look, so 10.
    /// </summary>
    [Theory]
    [InlineData("moonraker", 90, true)]
    [InlineData("moonraker", 180, false)]
    [InlineData("prusalink", 180, false)]
    [InlineData("bambu", 480, true)]
    [InlineData("bambu", 720, false)]
    public async Task TheSameJob_IsMatchedOnlyWithinTheKindsTolerance(string kind, int secondsApart, bool matched)
    {
        var s = await SetupAsync($"shared-tolerance-{kind}-{secondsApart}", kind: kind);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        var bPrintId = await IdOf(await Send(Create(s.B, s.PrinterId, bInstance, "41", started)));

        var aPrint = await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddSeconds(secondsApart)));

        Assert.Equal(matched ? HttpStatusCode.OK : HttpStatusCode.Created, aPrint.StatusCode);
        Assert.Equal(matched, await IdOf(aPrint) == bPrintId);
    }

    [Fact]
    public async Task AJob_WithoutBothFileNames_OrWithAnotherFile_IsNeverMatched()
    {
        var s = await SetupAsync("shared-names");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "41", started, fileName: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "42", started.AddSeconds(5), fileName: "other.gcode"))).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", started, fileName: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "8", started, fileName: "cube.gcode"))).StatusCode);
        Assert.Equal(4, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>A print that ended before this job started is another job (a failed first attempt), however close the starts.</summary>
    [Fact]
    public async Task AJob_IsNotMatched_ToAPrintThatEndedBeforeItStarted()
    {
        var s = await SetupAsync("shared-ended-before", kind: "bambu");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        var bPrintId = await IdOf(await Send(Create(s.B, s.PrinterId, bInstance, "41", started)));
        Assert.Equal(HttpStatusCode.OK, (await Send(Complete(s.B, bPrintId, new { status = PrintStatus.Failed, endedAt = started.AddMinutes(2) }))).StatusCode);

        var aPrint = await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddMinutes(5)));

        Assert.Equal(HttpStatusCode.Created, aPrint.StatusCode);
    }

    /// <summary>A job that ended before the bound bridge's print started is another job.</summary>
    [Fact]
    public async Task AJob_IsNotMatched_ToAPrintThatStartedAfterItEnded()
    {
        var s = await SetupAsync("shared-started-after-end");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "41", started.AddSeconds(90)))).StatusCode);

        var aPrint = await Send(Create(s.A, s.PrinterId, aInstance, "7", started, printTimeInSeconds: 60));

        Assert.Equal(HttpStatusCode.Created, aPrint.StatusCode);
    }

    /// <summary>
    /// Both bridges then complete the one print. The second complete is a replay of a finished
    /// print: one usage row per spool, the spool charged once, the status kept.
    /// </summary>
    [Fact]
    public async Task TwoBridgesCompletingTheSamePrint_LeaveItFinishedOnce()
    {
        var s = await SetupAsync("shared-two-completes");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var filament = await Json(await Send(UserJson(HttpMethod.Post, "/api/Filaments", s.A.Subject, new
        {
            displayName = $"Shared {Guid.NewGuid():N}",
            brand = "Test Brand",
            materialType = "PLA",
            colorName = "Green",
            colorHex = "00FF00",
            diameterMm = 1.75,
            initialNominalWeightMg = 1_000_000,
            materialDensityGramPerCubicCm = 1.24,
            isActive = true,
        })));
        var filamentId = filament.GetProperty("id").GetGuid();
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        var printId = await IdOf(await Send(Create(s.B, s.PrinterId, bInstance, "41", started)));
        Assert.Equal(printId, await IdOf(await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddSeconds(5)))));
        var ended = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, (await Send(Complete(s.B, printId, new { status = PrintStatus.Success, endedAt = ended, filamentUsage = new[] { new { filamentId, amountMg = 10_000 } } }))).StatusCode);
        var second = await Send(Complete(s.A, printId, new { status = PrintStatus.Success, endedAt = ended.AddSeconds(4), filamentUsage = new[] { new { filamentId, amountMg = 10_400 } } }));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var print = await Json(second);
        Assert.Equal((int)PrintStatus.Success, print.GetProperty("status").GetInt32());
        var row = Assert.Single(print.GetProperty("filamentUsage").EnumerateArray());
        Assert.Equal(10_400, row.GetProperty("amountMg").GetInt32());
        var remaining = (await GetAsUserAsync(_client, s.A.Subject, $"/api/Filaments/{filamentId}")).GetProperty("filamentRemaining").GetInt64();
        Assert.Equal(1_000_000 - 10_400, remaining);
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
    }

    [Fact]
    public async Task AClaim_FromABridgeThePrinterIsNotBoundTo_IsAccepted_AndCounted()
    {
        var s = await SetupAsync("shared-claim");
        await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var printId = await UploadedAsync(s);

        var claim = await Send(Claim(s.A, printId, instance, "9"));

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal($"{instance}:9", (await Json(claim)).GetProperty("externalId").GetString());
        Assert.Equal(1, await SharedAsync(s, s.A));
    }

    [Fact]
    public async Task AClaim_OfAJobTheBoundBridgeLogged_GetsThatPrint()
    {
        var s = await SetupAsync("shared-claim-same-job");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-5);
        var uploadedId = await UploadedAsync(s, started);
        var bPrintId = await IdOf(await Send(Create(s.B, s.PrinterId, bInstance, "41", started)));

        var claim = await Send(Claim(s.A, uploadedId, aInstance, "9", started.AddSeconds(10)));

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal(bPrintId, await IdOf(claim));
    }

    [Fact]
    public async Task TheBoundBridge_LogsAsUsual()
    {
        var s = await SetupAsync("shared-bound");
        var instance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(0, await SharedAsync(s, s.B));
    }

    /// <summary>A device that never checked in as a bridge (an older bridge, Home Assistant) has no bridge to count against.</summary>
    [Fact]
    public async Task ADeviceThatIsNotABridge_IsNotCounted()
    {
        var s = await SetupAsync("shared-not-bridge");
        var device = await PairAsync(_client, s.A.Subject, AllBridgeScopes, name: "older bridge");
        var token = device.GetProperty("token").GetString()!;

        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new { title = "old", printerId = s.PrinterId, externalSource = "printlog-bridge", externalId = "old:1" }),
        }, token));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, await SharedAsync(s, s.A));
    }

    [Fact]
    public async Task APrinterBoundToNoBridge_IsNotCounted()
    {
        var s = await SetupAsync("shared-unbound");
        var other = await CreatePrinterAsync(_client, s.A.Subject);
        var instance = await HeartbeatAsync(s.A, other);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, other, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(0, await SharedAsync(s, s.A));
    }

    // ---- After a move ----

    /// <summary>A job the old bridge was running when the printer moved is its to log (its outbox may send it late), and isn't counted.</summary>
    [Fact]
    public async Task AJobThatStartedBeforeTheMove_IsLoggedFromTheOldBridge_WithoutBeingCounted()
    {
        var s = await SetupAsync("move-inflight", boundTo: "a");
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var movedAt = await MoveToBAsync(s);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "1", movedAt.AddMinutes(-30)))).StatusCode);
        Assert.Equal(0, await SharedAsync(s, s.A));

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "2", movedAt.AddMinutes(1)))).StatusCode);
        Assert.Equal(1, await SharedAsync(s, s.A));
    }

    [Fact]
    public async Task AReplay_OfAPrintTheOldBridgeAlreadyLogged_ReturnsIt()
    {
        var s = await SetupAsync("move-replay", boundTo: "a");
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var created = await Send(Create(s.A, s.PrinterId, instance, "1", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await MoveToBAsync(s);

        var replay = await Send(Create(s.A, s.PrinterId, instance, "1", null));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(await IdOf(created), await IdOf(replay));
    }

    /// <summary>
    /// The old bridge stops watching within a minute of the move and never sees the job end; the
    /// new bridge finds it running at first look. It is handed the old bridge's print, relinked to
    /// its own job and connection, instead of logging a second one.
    /// </summary>
    [Fact]
    public async Task TheNewBridge_PickingUpTheRunningJob_IsHandedTheOldBridgesPrint()
    {
        var s = await SetupAsync("handoff", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var printId = await IdOf(await Send(Create(s.A, s.PrinterId, aInstance, "7", started)));
        await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        var bPrint = await Send(Create(s.B, s.PrinterId, bInstance, "41", started.AddSeconds(3)));

        Assert.Equal(HttpStatusCode.OK, bPrint.StatusCode);
        var handed = await Json(bPrint);
        Assert.Equal(printId, handed.GetProperty("id").GetInt64());
        Assert.Equal($"{bInstance}:41", handed.GetProperty("externalId").GetString());
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));

        // Its own replay now finds it by its own pair.
        Assert.Equal(printId, await IdOf(await Send(Create(s.B, s.PrinterId, bInstance, "41", started.AddSeconds(3)))));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connectionId = await db.Prints.Where(p => p.Id == printId).Select(p => p.ConnectionId).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(s.B.GrantId, await db.Connections.Where(c => c.Id == connectionId).Select(c => c.BridgeId).SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The new bridge's create names a connection it never registered: the print still moves to the new bridge's connection for the printer.</summary>
    [Fact]
    public async Task AHandOff_WithAnUnknownConnection_LinksTheNewBridgesConnectionForThePrinter()
    {
        var s = await SetupAsync("handoff-connection", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var printId = await IdOf(await Send(Create(s.A, s.PrinterId, aInstance, "7", started)));
        await MoveToBAsync(s);
        await HeartbeatAsync(s.B, s.PrinterId);

        var bPrint = await Send(Create(s.B, s.PrinterId, "unregistered", "41", started, connectionInstanceId: "unregistered"));

        Assert.Equal(HttpStatusCode.OK, bPrint.StatusCode);
        Assert.Equal(printId, await IdOf(bPrint));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connectionId = await db.Prints.Where(p => p.Id == printId).Select(p => p.ConnectionId).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(s.B.GrantId, await db.Connections.Where(c => c.Id == connectionId).Select(c => c.BridgeId).SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A job the new bridge started after the move never takes a print over: a reprint after the
    /// old job failed unseen is a new print, even within the 10-minute Bambu tolerance.
    /// </summary>
    [Fact]
    public async Task TheNewBridge_IsNotHandedAPrint_ForAJobThatStartedAfterTheMove()
    {
        var s = await SetupAsync("handoff-reprint", boundTo: "a", kind: "bambu");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var movedAt = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", movedAt.AddMinutes(-1)))).StatusCode);
        movedAt = await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "42", movedAt.AddMinutes(3)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "43", movedAt.AddMinutes(-1), fileName: "other.gcode"))).StatusCode);
        Assert.Equal(3, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>The old bridge's print for a job that started after the move is another job, whatever the new bridge's start says.</summary>
    [Fact]
    public async Task TheNewBridge_IsNotHandedAPrint_ThatStartedAfterTheMove()
    {
        var s = await SetupAsync("handoff-later-job", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var movedAt = await MoveToBAsync(s);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "8", movedAt.AddSeconds(50)))).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "44", movedAt.AddSeconds(60)))).StatusCode);
        Assert.Equal(2, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>An old bridge's print left Printing for more than 48 hours before the move is a stuck print, not the running job.</summary>
    [Fact]
    public async Task TheNewBridge_IsNotHandedAPrint_ThatStartedMoreThan48HoursBeforeTheMove()
    {
        var s = await SetupAsync("handoff-stuck", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddHours(-49);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", started))).StatusCode);
        await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "45", started.AddSeconds(20)))).StatusCode);
        Assert.Equal(2, await PrintCountAsync(s.PrinterId));
    }

    [Fact]
    public async Task TheNewBridge_IsNotHandedAPrint_WithoutFileNames()
    {
        var s = await SetupAsync("handoff-no-names", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", started, fileName: null))).StatusCode);
        await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "45", started))).StatusCode);
        Assert.Equal(2, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>
    /// Known limit: an adapter that reports its first look as the start (PrusaLink after a pause,
    /// Bambu Lab without <c>gcode_start_time</c>) misses the hand-off. A duplicate, never a merge;
    /// the old print stays Printing until the owner finishes it.
    /// </summary>
    [Fact]
    public async Task TheNewBridge_ReportingItsFirstLookAsTheStart_LogsANewPrint()
    {
        var s = await SetupAsync("handoff-first-look", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", DateTimeOffset.UtcNow.AddHours(-1)))).StatusCode);
        await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "46", DateTimeOffset.UtcNow.AddMinutes(1)))).StatusCode);
        Assert.Equal(2, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>The owner finishes a print the old bridge left Printing with the web app's print edit (a PATCH of its status).</summary>
    [Fact]
    public async Task APrintLeftPrintingByTheOldBridge_CanBeFinishedByTheOwner()
    {
        var s = await SetupAsync("handoff-owner-finishes", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var printId = await IdOf(await Send(Create(s.A, s.PrinterId, aInstance, "7", DateTimeOffset.UtcNow.AddHours(-1))));
        await MoveToBAsync(s);

        var patched = await Send(UserJson(HttpMethod.Patch, $"/api/Prints/{printId}", s.A.Subject, new { status = PrintStatus.Success }));

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal((int)PrintStatus.Success, (await Json(patched)).GetProperty("status").GetInt32());
    }

    /// <summary>The old bridge's late create for the job the new bridge already logged gets that print.</summary>
    [Fact]
    public async Task TheOldBridgesLateCreate_ForAJobTheNewBridgeLogged_GetsThatPrint()
    {
        var s = await SetupAsync("late", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var bPrint = await Send(Create(s.B, s.PrinterId, bInstance, "41", started));
        Assert.Equal(HttpStatusCode.Created, bPrint.StatusCode);

        var late = await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddSeconds(-2)));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        var print = await Json(late);
        Assert.Equal(await IdOf(bPrint), print.GetProperty("id").GetInt64());
        Assert.Equal($"{bInstance}:41", print.GetProperty("externalId").GetString());
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>Even once the new bridge has finished it: a second print would stay Printing for ever.</summary>
    [Fact]
    public async Task TheOldBridgesLateCreate_ForAJobTheNewBridgeAlreadyFinished_GetsThatPrint()
    {
        var s = await SetupAsync("late-finished", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var bPrintId = await IdOf(await Send(Create(s.B, s.PrinterId, bInstance, "41", started)));
        Assert.Equal(HttpStatusCode.OK, (await Send(Complete(s.B, bPrintId, new { status = PrintStatus.Success, endedAt = DateTimeOffset.UtcNow }))).StatusCode);

        var late = await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddSeconds(-2)));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal(bPrintId, await IdOf(late));
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>The old bridge's late create never gets a print that started after the move: that is the new bridge's next job.</summary>
    [Fact]
    public async Task TheOldBridgesLateCreate_IsNotMatched_ToAPrintThatStartedAfterTheMove()
    {
        var s = await SetupAsync("late-after-move", boundTo: "a", kind: "bambu");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var movedAt = await MoveToBAsync(s);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "41", movedAt.AddMinutes(3)))).StatusCode);

        var late = await Send(Create(s.A, s.PrinterId, aInstance, "7", movedAt.AddMinutes(-1)));

        Assert.Equal(HttpStatusCode.Created, late.StatusCode);
        Assert.Equal(2, await PrintCountAsync(s.PrinterId));
    }
}
