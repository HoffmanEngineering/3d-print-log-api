using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// The cross-bridge safety net (printlog-bridge#103, story 15a): a bridge that logs a new job for
/// a printer another of the user's bridges is bound to, and is evidently watching, is refused (403
/// <c>printer_watched_by_other_bridge</c>, which printlog-bridge drops without retrying) and
/// counted once per job on the bridge, so a misconfigured second bridge can't double-log. Without
/// evidence that the bound bridge is logging the printer, nothing is refused: printlog-bridge drops
/// a refused job for good, so a wrong refusal loses a print. A job that was already running when
/// the printer moved belongs to it: the old bridge may still log it, and the new bridge picking it
/// up is handed the same print.
/// </summary>
public class BridgeEventDedupeTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();

    private sealed record Setup(PairedBridge A, PairedBridge B, long PrinterId, long EntryId);

    /// <summary>Two bridges; the printer is connected to <paramref name="boundTo"/> ("a" or "b").</summary>
    private async Task<Setup> SetupAsync(string label, string boundTo = "b", string kind = "moonraker", string? bKey = null)
    {
        var a = await PairBridgeAsync(_client, label);
        var paired = await PairAsync(_client, a.Subject, AllBridgeScopes, name: "printlog-bridge on workshop");
        var b = new PairedBridge(a.Subject, paired.GetProperty("token").GetString()!, paired.GetProperty("id").GetInt64());
        await CheckInOkAsync(_client, a.Token, CheckInBody());
        await CheckInOkAsync(_client, b.Token, new { publicKey = bKey, networks = new[] { "192.168.2.0/24" } });
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
        return new Setup(a, b, printerId, (await Json(connected)).GetProperty("id").GetInt64());
    }

    /// <summary>The bridge's heartbeat for the printer: its watcher is running.</summary>
    private async Task<string> HeartbeatAsync(PairedBridge bridge, long printerId, string? lastError = null, DateTimeOffset? lastErrorAt = null, string? instanceId = null)
    {
        var created = instanceId is null;
        instanceId ??= Guid.NewGuid().ToString();
        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { kind = "moonraker", displayName = "voron", printerId, lastError, lastErrorAt }),
        }, bridge.Token));
        Assert.Equal(created ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
        return instanceId;
    }

    private static HttpRequestMessage Create(PairedBridge bridge, long printerId, string instanceId, string jobId, DateTimeOffset? startDate, string? fileName = "cube.gcode", string? connectionInstanceId = null)
        => WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new
            {
                title = "cube",
                printerId,
                fileName,
                startDate,
                status = PrintStatus.Printing,
                externalSource = "printlog-bridge",
                externalId = $"{instanceId}:{jobId}",
                connectionInstanceId = connectionInstanceId ?? instanceId,
            }),
        }, bridge.Token);

    private static HttpRequestMessage Claim(PairedBridge bridge, long printId, string instanceId, string jobId)
        => WithDevice(new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/claim")
        {
            Content = JsonContent.Create(new { externalSource = "printlog-bridge", externalId = $"{instanceId}:{jobId}", connectionInstanceId = instanceId, startDate = DateTimeOffset.UtcNow }),
        }, bridge.Token);

    private Task<HttpResponseMessage> Move(Setup s, PairedBridge from, PairedBridge to)
        => Send(UserJson(HttpMethod.Post, $"/api/bridges/{from.GrantId}/printers/{s.EntryId}/move", from.Subject, new { toBridgeId = to.GrantId, host = "192.168.2.30" }));

    private async Task<DateTimeOffset> MovedAtAsync(long entryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var moved = await db.BridgePrinters.Where(p => p.Id == entryId).Select(p => p.MovedAt).SingleAsync(TestContext.Current.CancellationToken);
        return new DateTimeOffset(DateTime.SpecifyKind(moved!.Value, DateTimeKind.Utc));
    }

    private async Task<int> PrintCountAsync(long printerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints.CountAsync(p => p.PrinterId == printerId, TestContext.Current.CancellationToken);
    }

    private async Task WithDbAsync(Func<PrintLogContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
    }

    private async Task<int> DroppedAsync(Setup s, PairedBridge bridge)
        => (await GetAsUserAsync(_client, s.A.Subject, $"/api/bridges/{bridge.GrantId}")).GetProperty("droppedEventCount").GetInt32();

    [Fact]
    public async Task ANewJob_FromABridgeThePrinterIsNotBoundTo_IsRefused_AndCounted()
    {
        var s = await SetupAsync("dedupe-refused");
        await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);

        var response = await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await Json(response);
        Assert.Equal("printer_watched_by_other_bridge", problem.GetProperty("code").GetString());
        Assert.Equal(s.B.GrantId, problem.GetProperty("bridgeId").GetInt64());
        Assert.Equal(0, await PrintCountAsync(s.PrinterId));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Create(s.A, s.PrinterId, instance, "2", null))).StatusCode);
        var a = await GetAsUserAsync(_client, s.A.Subject, $"/api/bridges/{s.A.GrantId}");
        Assert.Equal(2, a.GetProperty("droppedEventCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, a.GetProperty("lastDroppedEventAt").ValueKind);
        Assert.Equal(s.PrinterId, a.GetProperty("lastDroppedPrinterId").GetInt64());
        Assert.Equal(0, await DroppedAsync(s, s.B));
    }

    /// <summary>The bridge sends a refused job again when it ends (and may claim it): one job, counted once.</summary>
    [Fact]
    public async Task TheSameRefusedJob_IsCountedOnce()
    {
        var s = await SetupAsync("dedupe-count-once");
        await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var uploaded = await Send(UserJson(HttpMethod.Post, "/api/Prints", s.A.Subject, new { title = "uploaded", printerId = s.PrinterId, status = PrintStatus.Printing }));
        var uploadedId = (await Json(uploaded)).GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Claim(s.A, uploadedId, instance, "1"))).StatusCode);
        Assert.Equal(1, await DroppedAsync(s, s.A));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Create(s.A, s.PrinterId, instance, "2", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(2, await DroppedAsync(s, s.A));
    }

    [Fact]
    public async Task TheBoundBridge_LogsAsUsual()
    {
        var s = await SetupAsync("dedupe-bound");
        var instance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
    }

    /// <summary>The bound bridge hasn't started watching the printer (no heartbeat for it): nothing is logging it.</summary>
    [Fact]
    public async Task WhileTheBoundBridgeHasNoConnectionForThePrinter_AnotherBridgesJob_IsLogged()
    {
        var s = await SetupAsync("dedupe-no-connection");
        var instance = await HeartbeatAsync(s.A, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(0, await DroppedAsync(s, s.A));
    }

    /// <summary>While the bound bridge's watcher is stale nothing else logs the printer, so a print is never lost to the safety net.</summary>
    [Fact]
    public async Task WhileTheBoundBridgesConnectionIsStale_AnotherBridgesJob_IsLogged()
    {
        var s = await SetupAsync("dedupe-stale");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        await WithDbAsync(db => db.Connections.Where(c => c.InstanceId == bInstance)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastSeenAt, DateTime.UtcNow.AddHours(-1)), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
    }

    /// <summary>A watcher that reports a recent error may not be logging; an old error doesn't count.</summary>
    [Fact]
    public async Task WhileTheBoundBridgeReportsARecentError_AnotherBridgesJob_IsLogged()
    {
        var s = await SetupAsync("dedupe-error");
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId, "can't reach 192.168.2.30:7125", DateTimeOffset.UtcNow.AddMinutes(-1));
        var instance = await HeartbeatAsync(s.A, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);

        await HeartbeatAsync(s.B, s.PrinterId, "can't reach 192.168.2.30:7125", DateTimeOffset.UtcNow.AddHours(-2), bInstance);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Create(s.A, s.PrinterId, instance, "2", DateTimeOffset.UtcNow))).StatusCode);
    }

    [Fact]
    public async Task WhileTheBoundEntryHasCredentialIssues_AnotherBridgesJob_IsLogged()
    {
        var s = await SetupAsync("dedupe-issues");
        await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        await WithDbAsync(db => db.BridgePrinters.Where(p => p.Id == s.EntryId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.CredentialIssues, "moonraker_api_key:rejected_by_printer"), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
    }

    /// <summary>
    /// A Bambu Lab printer can't be watched without its access code sealed to the bridge's current
    /// key: missing or stale, the bound bridge isn't logging it, whatever its heartbeat says.
    /// </summary>
    [Fact]
    public async Task ABoundPrinterWithoutItsRequiredCredential_DoesNotBlockAnotherBridge()
    {
        using var key = new BridgeSealedCredentialTests.BridgeKey();
        var s = await SetupAsync("dedupe-credential", kind: "bambu", bKey: key.PublicKey);
        await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "missing", DateTimeOffset.UtcNow))).StatusCode);

        await WithDbAsync(async db =>
        {
            db.BridgePrinterSecrets.Add(new BridgePrinterSecret { BridgePrinterId = s.EntryId, Field = "access_code", SealedValue = "v1.stale", KeyFingerprint = new string('0', 64), UpdatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "stale", DateTimeOffset.UtcNow))).StatusCode);

        await WithDbAsync(db => db.BridgePrinterSecrets.Where(x => x.BridgePrinterId == s.EntryId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.KeyFingerprint, key.Fingerprint), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Create(s.A, s.PrinterId, instance, "sealed", DateTimeOffset.UtcNow))).StatusCode);
    }

    /// <summary>A job the old bridge was running when the printer moved is still its to log (its outbox may send it late).</summary>
    [Fact]
    public async Task AJobThatStartedBeforeTheMove_IsAcceptedFromTheOldBridge()
    {
        var s = await SetupAsync("dedupe-inflight", boundTo: "a");
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, instance, "1", started))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Create(s.A, s.PrinterId, instance, "2", DateTimeOffset.UtcNow.AddMinutes(1)))).StatusCode);
    }

    /// <summary>The same pair again is the same print: a replay is never refused.</summary>
    [Fact]
    public async Task AReplay_OfAPrintTheOldBridgeAlreadyLogged_IsNeverRefused()
    {
        var s = await SetupAsync("dedupe-replay", boundTo: "a");
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var created = await Send(Create(s.A, s.PrinterId, instance, "1", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        await HeartbeatAsync(s.B, s.PrinterId);

        var replay = await Send(Create(s.A, s.PrinterId, instance, "1", null));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal((await Json(created)).GetProperty("id").GetInt64(), (await Json(replay)).GetProperty("id").GetInt64());
    }

    /// <summary>
    /// The old bridge stops watching within a minute of the move and never sees the job end; the
    /// new bridge finds it running at first look. It is handed the old bridge's print, relinked to
    /// its own job and connection, instead of logging a second one.
    /// </summary>
    [Fact]
    public async Task TheNewBridge_PickingUpTheRunningJob_IsHandedTheOldBridgesPrint()
    {
        var s = await SetupAsync("dedupe-handoff", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var aPrint = await Send(Create(s.A, s.PrinterId, aInstance, "7", started));
        Assert.Equal(HttpStatusCode.Created, aPrint.StatusCode);
        var printId = (await Json(aPrint)).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        var bPrint = await Send(Create(s.B, s.PrinterId, bInstance, "41", started.AddSeconds(3)));

        Assert.Equal(HttpStatusCode.OK, bPrint.StatusCode);
        var handed = await Json(bPrint);
        Assert.Equal(printId, handed.GetProperty("id").GetInt64());
        Assert.Equal($"{bInstance}:41", handed.GetProperty("externalId").GetString());
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));

        // Its own replay now finds it by its own pair.
        Assert.Equal(printId, (await Json(await Send(Create(s.B, s.PrinterId, bInstance, "41", started.AddSeconds(3))))).GetProperty("id").GetInt64());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connectionId = await db.Prints.Where(p => p.Id == printId).Select(p => p.ConnectionId).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(s.B.GrantId, await db.Connections.Where(c => c.Id == connectionId).Select(c => c.BridgeId).SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The new bridge's create names a connection it never registered: the print still moves to the new bridge's connection for the printer.</summary>
    [Fact]
    public async Task AHandOff_WithAnUnknownConnection_LinksTheNewBridgesConnectionForThePrinter()
    {
        var s = await SetupAsync("dedupe-handoff-connection", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        var printId = (await Json(await Send(Create(s.A, s.PrinterId, aInstance, "7", started)))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        await HeartbeatAsync(s.B, s.PrinterId);

        var bPrint = await Send(Create(s.B, s.PrinterId, "unregistered", "41", started, connectionInstanceId: "unregistered"));

        Assert.Equal(HttpStatusCode.OK, bPrint.StatusCode);
        Assert.Equal(printId, (await Json(bPrint)).GetProperty("id").GetInt64());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connectionId = await db.Prints.Where(p => p.Id == printId).Select(p => p.ConnectionId).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(s.B.GrantId, await db.Connections.Where(c => c.Id == connectionId).Select(c => c.BridgeId).SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A job the new bridge started after the move is a new print, even with the old one left open.</summary>
    [Fact]
    public async Task TheNewBridge_IsNotHandedAPrint_ForAJobThatStartedAfterTheMove()
    {
        var s = await SetupAsync("dedupe-no-handoff", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", started))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "42", DateTimeOffset.UtcNow.AddMinutes(30)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "43", started, fileName: "other.gcode"))).StatusCode);
        Assert.Equal(3, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>
    /// The old bridge's print for a job that started well after the move (while the new bridge had
    /// no connection yet) is another job, however close the new bridge's start time is.
    /// </summary>
    [Fact]
    public async Task TheNewBridge_IsNotHandedAPrint_ThatStartedWellAfterTheMove()
    {
        var s = await SetupAsync("dedupe-later-job", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var movedAt = await MovedAtAsync(s.EntryId);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "8", movedAt.AddMinutes(12)))).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "44", movedAt.AddMinutes(9)))).StatusCode);
        Assert.Equal(2, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>Without both file names the job can't be told apart from another: no hand-off.</summary>
    [Fact]
    public async Task TheNewBridge_IsNotHandedAPrint_WithoutFileNames()
    {
        var s = await SetupAsync("dedupe-no-names", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", started, fileName: null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "45", started))).StatusCode);
        Assert.Equal(2, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>
    /// Some adapters can't report when a job started (PrusaLink after a pause, Bambu Lab without
    /// its start time) and give their first look instead. Within the grace window after a move, the
    /// old bridge's one open print of the same file, started before the move, is the job.
    /// </summary>
    [Fact]
    public async Task TheNewBridge_ReportingItsFirstLookAsTheStart_IsHandedTheOnlyOpenPrintOfThatFile()
    {
        var s = await SetupAsync("dedupe-first-look", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var aPrint = await Send(Create(s.A, s.PrinterId, aInstance, "7", DateTimeOffset.UtcNow.AddHours(-1)));
        var printId = (await Json(aPrint)).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        var bPrint = await Send(Create(s.B, s.PrinterId, bInstance, "46", DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.Equal(HttpStatusCode.OK, bPrint.StatusCode);
        Assert.Equal(printId, (await Json(bPrint)).GetProperty("id").GetInt64());
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
    }

    [Fact]
    public async Task TheNewBridge_ReportingItsFirstLook_IsNotHandedAPrint_WhenTheOldBridgeLeftSeveralOpen()
    {
        var s = await SetupAsync("dedupe-first-look-two", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "7", DateTimeOffset.UtcNow.AddHours(-3)))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, s.PrinterId, aInstance, "8", DateTimeOffset.UtcNow.AddHours(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.B, s.PrinterId, bInstance, "47", DateTimeOffset.UtcNow.AddMinutes(1)))).StatusCode);
        Assert.Equal(3, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>The old bridge's late create for the job the new bridge already logged gets that print.</summary>
    [Fact]
    public async Task TheOldBridgesLateCreate_ForAJobTheNewBridgeLogged_GetsThatPrint()
    {
        var s = await SetupAsync("dedupe-late", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var bPrint = await Send(Create(s.B, s.PrinterId, bInstance, "41", started));
        Assert.Equal(HttpStatusCode.Created, bPrint.StatusCode);

        var late = await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddSeconds(-2)));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        var print = await Json(late);
        Assert.Equal((await Json(bPrint)).GetProperty("id").GetInt64(), print.GetProperty("id").GetInt64());
        Assert.Equal($"{bInstance}:41", print.GetProperty("externalId").GetString());
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
    }

    /// <summary>Even once the new bridge has finished it: a second print would stay Printing for ever.</summary>
    [Fact]
    public async Task TheOldBridgesLateCreate_ForAJobTheNewBridgeAlreadyFinished_GetsThatPrint()
    {
        var s = await SetupAsync("dedupe-late-finished", boundTo: "a");
        var aInstance = await HeartbeatAsync(s.A, s.PrinterId);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        Assert.Equal(HttpStatusCode.OK, (await Move(s, s.A, s.B)).StatusCode);
        var bInstance = await HeartbeatAsync(s.B, s.PrinterId);
        var bPrintId = (await Json(await Send(Create(s.B, s.PrinterId, bInstance, "41", started)))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{bPrintId}/complete")
        {
            Content = JsonContent.Create(new { status = PrintStatus.Success, endedAt = DateTimeOffset.UtcNow }),
        }, s.B.Token))).StatusCode);

        var late = await Send(Create(s.A, s.PrinterId, aInstance, "7", started.AddSeconds(-2)));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal(bPrintId, (await Json(late)).GetProperty("id").GetInt64());
        Assert.Equal(1, await PrintCountAsync(s.PrinterId));
    }

    [Fact]
    public async Task AClaim_FromABridgeThePrinterIsNotBoundTo_IsRefused()
    {
        var s = await SetupAsync("dedupe-claim");
        await HeartbeatAsync(s.B, s.PrinterId);
        var instance = await HeartbeatAsync(s.A, s.PrinterId);
        var uploaded = await Send(UserJson(HttpMethod.Post, "/api/Prints", s.A.Subject, new { title = "uploaded", printerId = s.PrinterId, status = PrintStatus.Printing }));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var printId = (await Json(uploaded)).GetProperty("id").GetInt64();

        var claim = await Send(Claim(s.A, printId, instance, "9"));

        Assert.Equal(HttpStatusCode.Forbidden, claim.StatusCode);
        Assert.Equal("printer_watched_by_other_bridge", await CodeOf(claim));
        Assert.Equal(1, await DroppedAsync(s, s.A));
    }

    /// <summary>A device that never checked in as a bridge (an older bridge, Home Assistant) has no bridge to count against, and isn't guarded.</summary>
    [Fact]
    public async Task ADeviceThatIsNotABridge_IsNotGuarded()
    {
        var s = await SetupAsync("dedupe-not-bridge");
        await HeartbeatAsync(s.B, s.PrinterId);
        var device = await PairAsync(_client, s.A.Subject, AllBridgeScopes, name: "older bridge");
        var token = device.GetProperty("token").GetString()!;

        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new { title = "old", printerId = s.PrinterId, externalSource = "printlog-bridge", externalId = "old:1" }),
        }, token));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>A printer bound to no bridge from the app (config files only) is not guarded.</summary>
    [Fact]
    public async Task APrinterBoundToNoBridge_IsNotGuarded()
    {
        var s = await SetupAsync("dedupe-unbound");
        var other = await CreatePrinterAsync(_client, s.A.Subject);
        var instance = await HeartbeatAsync(s.A, other);

        Assert.Equal(HttpStatusCode.Created, (await Send(Create(s.A, other, instance, "1", DateTimeOffset.UtcNow))).StatusCode);
    }
}
