using System.Net;
using System.Text.Json;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.UserApiKeys;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// <c>GET /api/sync/snapshot</c> (#234): everything a Home Assistant integration renders, in one
/// response. Asserted on the wire (JSON names), because the integration parses it by name.
/// </summary>
public class SyncSnapshotTests(SyncTestFactory factory) : IClassFixture<SyncTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> SnapshotAsync(User user, string query = "")
    {
        var response = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot" + query), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
    }

    private async Task<T> WithDbAsync<T>(Func<PrintLogContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var result = await action(db);
        await db.SaveChangesAsync(Ct);
        return result;
    }

    private Task<Printer> AddPrinterAsync(User user, string name, int slotCount = 1, bool active = true) =>
        WithDbAsync(db =>
        {
            var printer = new Printer { Name = name, UserId = user.Id, IsActive = active, CategoryNickname = "FDM", SlotCount = slotCount, Make = "LDO", Model = "Voron 2.4" };
            db.Printers.Add(printer);
            return Task.FromResult(printer);
        });

    private Task<Filament> AddSpoolAsync(User user, string name, long? initialMg, int? spoolNumber, List<string>? colors = null, string? colorHex = null, bool active = true) =>
        WithDbAsync(db =>
        {
            var filament = new Filament
            {
                Id = Guid.NewGuid(),
                DisplayName = name,
                Brand = "Prusament",
                MaterialType = "PLA",
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.24,
                DiameterMm = 1.75,
                InitialNominalWeightMg = initialMg,
                SpoolNumber = spoolNumber,
                Colors = colors,
                ColorHex = colorHex,
                ColorName = "Galaxy Black",
                StorageLocation = "Dry box 1",
                IsActive = active,
                CreatedById = user.Id,
                UpdatedById = user.Id,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow,
            };
            db.Filaments.Add(filament);
            return Task.FromResult(filament);
        });

    private Task<Print> AddPrintAsync(User user, Printer printer, Print.PrintStatus status, DateTimeOffset? start,
        int? estimateSeconds = null, int? printSeconds = null, string? title = null, (Filament Spool, int Mg)? usage = null,
        PrintSource source = PrintSource.Unknown, (int? Slot, Filament? Spool, int? EstimatedMg)[]? estimates = null) =>
        WithDbAsync(async db =>
        {
            var print = new Print
            {
                Title = title ?? $"{status} print",
                PrinterId = printer.Id,
                Status = status,
                StartDate = start,
                EstimatedPrintTimeInSeconds = estimateSeconds,
                PrintTimeInSeconds = printSeconds,
                ViewStatus = Print.PrintViewStatus.Private,
                Source = source,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(Ct);
            foreach (var e in estimates ?? [])
            {
                db.PrintFilament.Add(new PrintFilament { PrintId = print.Id, Slot = e.Slot, FilamentId = e.Spool?.Id, EstimatedAmountMg = e.EstimatedMg, Source = PrintFilament.SourceMeasurement.Weight });
            }

            if (usage is { } u)
            {
                db.PrintFilament.Add(new PrintFilament { PrintId = print.Id, FilamentId = u.Spool.Id, AmountMg = u.Mg, Source = PrintFilament.SourceMeasurement.Weight });
            }

            return print;
        });

    private Task LoadAsync(Printer printer, Filament spool, int? slot, string? label = null, bool unloaded = false) =>
        WithDbAsync(db =>
        {
            db.PrinterFilament.Add(new PrinterFilament
            {
                PrinterId = printer.Id,
                FilamentId = spool.Id,
                Slot = slot,
                SlotLabel = label,
                LoadedDateTime = SyncTestFactory.Start.AddDays(-1),
                UnloadedDateTime = unloaded ? SyncTestFactory.Start.AddHours(-1) : null,
            });
            return Task.FromResult(0);
        });

    private Task AddConnectionAsync(User user, Printer printer, DateTimeOffset lastSeen, string kind = "moonraker") =>
        WithDbAsync(db =>
        {
            db.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Kind = kind,
                InstanceId = Guid.NewGuid().ToString("N"),
                DisplayName = "bridge",
                PrinterId = printer.Id,
                CreatedDate = lastSeen.UtcDateTime,
                LastSeenAt = lastSeen.UtcDateTime,
            });
            return Task.FromResult(0);
        });

    private Task<PrinterMaintenance> AddMaintenanceAsync(User user, Printer printer, DateTimeOffset date, bool done, string? category = null, string? description = null) =>
        WithDbAsync(db =>
        {
            var entry = new PrinterMaintenance { PrinterId = printer.Id, CreatedById = user.Id, UpdatedById = user.Id, CreatedDate = DateTime.UtcNow, UpdatedDate = DateTime.UtcNow, Date = date, Done = done, Category = category, Description = description };
            db.PrinterMaintenance.Add(entry);
            return Task.FromResult(entry);
        });

    private static JsonElement Printer(JsonElement snapshot, long id) =>
        snapshot.GetProperty("printers").EnumerateArray().Single(p => p.GetProperty("id").GetInt64() == id);

    private static JsonElement Spool(JsonElement snapshot, Guid id) =>
        snapshot.GetProperty("spools").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Snapshot_IsVersionedAndCarriesTheVersion()
    {
        var user = await factory.CreateUserAsync();

        var snapshot = await SnapshotAsync(user);
        var version = JsonDocument.Parse(await (await _client.SendAsync(factory.Get(user, "/api/sync/version"), Ct)).Content.ReadAsStringAsync(Ct))
            .RootElement.GetProperty("version").GetString();

        Assert.Equal(1, snapshot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(version, snapshot.GetProperty("version").GetString());
        Assert.Empty(snapshot.GetProperty("printers").EnumerateArray());
        Assert.Empty(snapshot.GetProperty("spools").EnumerateArray());
    }

    [Fact]
    public async Task Snapshot_ReportsEachPrinter()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron", slotCount: 4);
        var retired = await AddPrinterAsync(user, "Ender", active: false);

        var snapshot = await SnapshotAsync(user);

        var p = Printer(snapshot, printer.Id);
        Assert.Equal("Voron", p.GetProperty("name").GetString());
        Assert.Equal("LDO", p.GetProperty("make").GetString());
        Assert.Equal("Voron 2.4", p.GetProperty("model").GetString());
        Assert.True(p.GetProperty("active").GetBoolean());
        Assert.Equal(4, p.GetProperty("slotCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("connection").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("currentPrint").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("lastFinishedPrint").ValueKind);
        Assert.Empty(p.GetProperty("loaded").EnumerateArray());
        Assert.False(Printer(snapshot, retired.Id).GetProperty("active").GetBoolean());
        // Ordered by name, so the body is deterministic.
        Assert.Equal(new[] { "Ender", "Voron" }, snapshot.GetProperty("printers").EnumerateArray().Select(x => x.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Snapshot_CurrentPrint_IsTheNewestPrintingPrint()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var start = SyncTestFactory.Start.AddMinutes(-30);
        await AddPrintAsync(user, printer, Print.PrintStatus.Printing, start.AddDays(-3), title: "Abandoned");
        var current = await AddPrintAsync(user, printer, Print.PrintStatus.Printing, start, estimateSeconds: 3600, title: "Benchy");

        var c = Printer(await SnapshotAsync(user), printer.Id).GetProperty("currentPrint");

        Assert.Equal(current.Id, c.GetProperty("id").GetInt64());
        Assert.Equal("Benchy", c.GetProperty("title").GetString());
        Assert.Equal(start, c.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(3600, c.GetProperty("estimatedSeconds").GetInt32());
        Assert.Equal(start.AddHours(1), c.GetProperty("estimatedEndAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Snapshot_LastFinishedPrint_IsTheNewestFinishedPrint_WithItsFinishTime()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var start = SyncTestFactory.Start.AddHours(-5);
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, start.AddDays(-1), printSeconds: 600);
        var failed = await AddPrintAsync(user, printer, Print.PrintStatus.Failed, start, printSeconds: 2400, title: "Spaghetti");
        await AddPrintAsync(user, printer, Print.PrintStatus.Printing, start.AddHours(1));
        await AddPrintAsync(user, printer, Print.PrintStatus.Pending, start.AddHours(2));

        var last = Printer(await SnapshotAsync(user), printer.Id).GetProperty("lastFinishedPrint");

        Assert.Equal(failed.Id, last.GetProperty("id").GetInt64());
        Assert.Equal("Spaghetti", last.GetProperty("title").GetString());
        Assert.Equal("failed", last.GetProperty("status").GetString());
        Assert.Equal(start, last.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(start.AddMinutes(40), last.GetProperty("finishedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(Print.PrintStatus.Success, "success")]
    [InlineData(Print.PrintStatus.Cancelled, "canceled")]
    [InlineData(Print.PrintStatus.PartialSuccess, "partial_success")]
    public async Task Snapshot_LastFinishedPrint_NamesItsStatus(Print.PrintStatus status, string expected)
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        await AddPrintAsync(user, printer, status, start: null);

        var last = Printer(await SnapshotAsync(user), printer.Id).GetProperty("lastFinishedPrint");

        Assert.Equal(expected, last.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task Snapshot_Spools_ReportRemainingAndUsedFromTheSharedFigures()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var spool = await AddSpoolAsync(user, "Galaxy Black", 1_000_000, 7, colors: ["#1a1a1a", "nonsense", "FFFFFF"]);
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, SyncTestFactory.Start.AddDays(-40), usage: (spool, 187_640));

        var s = Spool(await SnapshotAsync(user), spool.Id);

        Assert.Equal(7, s.GetProperty("spoolNumber").GetInt32());
        Assert.Equal("Galaxy Black", s.GetProperty("name").GetString());
        Assert.Equal("Prusament", s.GetProperty("brand").GetString());
        Assert.Equal("PLA", s.GetProperty("material").GetString());
        Assert.Equal("filament", s.GetProperty("category").GetString());
        Assert.Equal("Galaxy Black", s.GetProperty("colorName").GetString());
        Assert.Equal("#1A1A1A", s.GetProperty("colorHex").GetString());
        Assert.Equal(new[] { "#1A1A1A", "#FFFFFF" }, s.GetProperty("colors").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(812.4, s.GetProperty("remainingGrams").GetDouble());
        Assert.Equal(187.6, s.GetProperty("usedGrams").GetDouble());
        Assert.Equal(1000, s.GetProperty("initialGrams").GetDouble());
        Assert.Equal("Dry box 1", s.GetProperty("storageLocation").GetString());
        Assert.True(s.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Snapshot_UntrackedSpool_ReportsNullRemaining_NotANegativeBalance()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var untracked = await AddSpoolAsync(user, "Untracked", initialMg: null, spoolNumber: 1, colorHex: "#ff0000");
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, SyncTestFactory.Start.AddDays(-40), usage: (untracked, 50_000));

        var s = Spool(await SnapshotAsync(user), untracked.Id);

        Assert.Equal(JsonValueKind.Null, s.GetProperty("remainingGrams").ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("initialGrams").ValueKind);
        Assert.Equal(50, s.GetProperty("usedGrams").GetDouble());
        Assert.Equal("#FF0000", s.GetProperty("colorHex").GetString());
        Assert.Equal(new[] { "#FF0000" }, s.GetProperty("colors").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Snapshot_OverconsumedSpool_ClampsRemainingAtZero()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var spool = await AddSpoolAsync(user, "Empty", 100_000, 1);
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, SyncTestFactory.Start.AddDays(-40), usage: (spool, 150_000));

        var s = Spool(await SnapshotAsync(user), spool.Id);

        Assert.Equal(0, s.GetProperty("remainingGrams").GetDouble());
    }

    [Fact]
    public async Task Snapshot_Loaded_ListsWhatIsInEachSlotNow_InSlotOrder()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron", slotCount: 4);
        var black = await AddSpoolAsync(user, "Black", 1_000_000, 1, colors: ["#000000"]);
        var white = await AddSpoolAsync(user, "White", null, 2, colors: ["#FFFFFF"]);
        var old = await AddSpoolAsync(user, "Old", 1_000_000, 3);
        await LoadAsync(printer, white, 2, "T2");
        await LoadAsync(printer, black, 0, "T0");
        await LoadAsync(printer, old, 1, "T1", unloaded: true);

        var loaded = Printer(await SnapshotAsync(user), printer.Id).GetProperty("loaded").EnumerateArray().ToList();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(0, loaded[0].GetProperty("slot").GetInt32());
        Assert.Equal("T0", loaded[0].GetProperty("slotLabel").GetString());
        Assert.Equal(black.Id, loaded[0].GetProperty("spoolId").GetGuid());
        Assert.Equal(1, loaded[0].GetProperty("spoolNumber").GetInt32());
        Assert.Equal("Black", loaded[0].GetProperty("name").GetString());
        Assert.Equal("PLA", loaded[0].GetProperty("material").GetString());
        Assert.Equal("#000000", loaded[0].GetProperty("colorHex").GetString());
        Assert.Equal(new[] { "#000000" }, loaded[0].GetProperty("colors").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(1000, loaded[0].GetProperty("remainingGrams").GetDouble());
        Assert.Equal(white.Id, loaded[1].GetProperty("spoolId").GetGuid());
        Assert.Equal(JsonValueKind.Null, loaded[1].GetProperty("remainingGrams").ValueKind);
    }

    [Fact]
    public async Task Snapshot_LeavesOutOtherUsersData()
    {
        var user = await factory.CreateUserAsync();
        var other = await factory.CreateUserAsync();
        var mine = await AddPrinterAsync(user, "Mine");
        var theirs = await AddPrinterAsync(other, "Theirs");
        var theirSpool = await AddSpoolAsync(other, "Theirs", 1_000_000, 1);
        await AddPrintAsync(other, theirs, Print.PrintStatus.Printing, SyncTestFactory.Start);
        // A corrupt row: another user's spool loaded in my printer.
        await LoadAsync(mine, theirSpool, 0);

        var snapshot = await SnapshotAsync(user);

        var printer = Assert.Single(snapshot.GetProperty("printers").EnumerateArray());
        Assert.Equal(mine.Id, printer.GetProperty("id").GetInt64());
        Assert.Empty(printer.GetProperty("loaded").EnumerateArray());
        Assert.Empty(snapshot.GetProperty("spools").EnumerateArray());
    }

    [Fact]
    public async Task Snapshot_Connection_IsOnlineUntil15MinutesWithoutAHeartbeat()
    {
        var user = await factory.CreateUserAsync();
        var fresh = await AddPrinterAsync(user, "Fresh");
        var stale = await AddPrinterAsync(user, "Stale");
        await AddConnectionAsync(user, fresh, SyncTestFactory.Start.AddMinutes(-14));
        await AddConnectionAsync(user, stale, SyncTestFactory.Start.AddMinutes(-15));
        factory.Clock.SetUtcNow(SyncTestFactory.Start);

        var snapshot = await SnapshotAsync(user);

        var connection = Printer(snapshot, fresh.Id).GetProperty("connection");
        Assert.Equal("moonraker", connection.GetProperty("kind").GetString());
        Assert.Equal("online", connection.GetProperty("status").GetString());
        Assert.Equal("stale", Printer(snapshot, stale.Id).GetProperty("connection").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Snapshot_Connection_UsesTheFreshestOfSeveral()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        await AddConnectionAsync(user, printer, SyncTestFactory.Start.AddDays(-2), kind: "octoprint");
        await AddConnectionAsync(user, printer, SyncTestFactory.Start.AddMinutes(-1));
        factory.Clock.SetUtcNow(SyncTestFactory.Start);

        var connection = Printer(await SnapshotAsync(user), printer.Id).GetProperty("connection");

        Assert.Equal("moonraker", connection.GetProperty("kind").GetString());
        Assert.Equal("online", connection.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Snapshot_ETag_Returns304UntilAWrite()
    {
        var user = await factory.CreateUserAsync();
        await AddPrinterAsync(user, "Voron");
        factory.Clock.SetUtcNow(SyncTestFactory.Start);

        var first = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        // Weak: response compression serves several encodings of one body under this tag.
        Assert.True(etag.IsWeak);
        Assert.True(first.Headers.CacheControl?.Private);

        var unchanged = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", etag.Tag), Ct);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(etag.Tag, unchanged.Headers.ETag?.Tag);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync(Ct));

        factory.Invalidate(user);
        var changed = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", etag.Tag), Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag.Tag, changed.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Snapshot_ETag_ChangesWhenAConnectionGoesStale_WithoutAWrite()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        await AddConnectionAsync(user, printer, SyncTestFactory.Start.AddMinutes(-5));
        factory.Clock.SetUtcNow(SyncTestFactory.Start);
        var online = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct);

        factory.Clock.SetUtcNow(SyncTestFactory.Start.AddMinutes(9));
        var stillOnline = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", online.Headers.ETag!.Tag), Ct);
        factory.Clock.SetUtcNow(SyncTestFactory.Start.AddMinutes(10));
        var stale = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", online.Headers.ETag.Tag), Ct);
        factory.Clock.SetUtcNow(SyncTestFactory.Start);

        Assert.Equal(HttpStatusCode.NotModified, stillOnline.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("stale", Printer(body, printer.Id).GetProperty("connection").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Snapshot_IsCachedOnTheVersion()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Before");
        await SnapshotAsync(user);

        // Written behind the API's back: no version bump, so the cached snapshot stands.
        await WithDbAsync(async db =>
        {
            var row = await db.Printers.FindAsync([printer.Id], Ct);
            row!.Name = "After";
            return 0;
        });
        var cached = await SnapshotAsync(user);
        factory.Invalidate(user);
        var fresh = await SnapshotAsync(user);

        Assert.Equal("Before", Printer(cached, printer.Id).GetProperty("name").GetString());
        Assert.Equal("After", Printer(fresh, printer.Id).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Snapshot_AccountTotals_CountThisMonthInTheRequestedTimeZone()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var spool = await AddSpoolAsync(user, "Black", 1_000_000, 1);
        factory.Clock.SetUtcNow(SyncTestFactory.Start); // 2026-10-16 12:00 UTC
        // 2026-09-30 23:00 UTC is already October in Berlin (UTC+2), but September in UTC.
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), usage: (spool, 10_000));
        await AddPrintAsync(user, printer, Print.PrintStatus.Failed, new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero), usage: (spool, 2_250));
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero), usage: (spool, 99_000));
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, start: null, usage: (spool, 99_000));

        var utc = (await SnapshotAsync(user)).GetProperty("account");
        var berlin = (await SnapshotAsync(user, "?timeZone=Europe/Berlin")).GetProperty("account");

        Assert.Equal("2026-10", utc.GetProperty("month").GetString());
        Assert.Equal("UTC", utc.GetProperty("timeZone").GetString());
        Assert.Equal(1, utc.GetProperty("printCount").GetInt32());
        Assert.Equal(2.3, utc.GetProperty("grams").GetDouble());
        Assert.Equal("Europe/Berlin", berlin.GetProperty("timeZone").GetString());
        Assert.Equal(2, berlin.GetProperty("printCount").GetInt32());
        Assert.Equal(12.3, berlin.GetProperty("grams").GetDouble());
    }

    [Fact]
    public async Task Snapshot_AccountTotals_RollOverAtTheMonthWithoutAWrite()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, SyncTestFactory.Start);
        factory.Clock.SetUtcNow(SyncTestFactory.Start);
        var october = (await SnapshotAsync(user)).GetProperty("account");

        factory.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.Zero));
        var november = (await SnapshotAsync(user)).GetProperty("account");
        factory.Clock.SetUtcNow(SyncTestFactory.Start);

        Assert.Equal(1, october.GetProperty("printCount").GetInt32());
        Assert.Equal("2026-11", november.GetProperty("month").GetString());
        Assert.Equal(0, november.GetProperty("printCount").GetInt32());
    }

    [Fact]
    public async Task Snapshot_UnknownTimeZone_Returns400()
    {
        var user = await factory.CreateUserAsync();

        var response = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot?timeZone=Mars/Olympus"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Snapshot_Unauthenticated_Returns401()
    {
        var response = await _client.GetAsync("/api/sync/snapshot", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Snapshot_AcceptsAnApiKey()
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        create.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "sync snapshot" });
        var key = await (await _client.SendAsync(create, Ct)).Content.ReadFromJsonAsync<NewUserApiKeyDto>(Ct);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sync/snapshot");
        request.Headers.Add("X-Api-Key", key!.PublicKey);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Contains(body.GetProperty("printers").EnumerateArray(), p => p.GetProperty("id").GetInt64() == IntegrationTestSeeder.TestPrinterId);
    }

    [Fact]
    public async Task Snapshot_PrintTimeSeconds_MatchesAnalyticsAllTime()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, SyncTestFactory.Start.AddDays(-3), printSeconds: 3600);
        await AddPrintAsync(user, printer, Print.PrintStatus.Failed, SyncTestFactory.Start.AddDays(-2), printSeconds: 1800);
        await AddPrintAsync(user, printer, Print.PrintStatus.Printing, SyncTestFactory.Start.AddHours(-1), estimateSeconds: 600);
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, null);

        var analytics = await _client.SendAsync(factory.Get(user, "/api/analytics/printers?timeZone=UTC"), Ct);
        Assert.Equal(HttpStatusCode.OK, analytics.StatusCode);
        var expected = JsonDocument.Parse(await analytics.Content.ReadAsStringAsync(Ct)).RootElement
            .GetProperty("printers").EnumerateArray()
            .Single(r => r.GetProperty("printerId").GetInt64() == printer.Id)
            .GetProperty("printTimeSeconds").GetInt64();

        var snapshot = await SnapshotAsync(user);

        Assert.True(expected >= 5400);
        Assert.Equal(expected, Printer(snapshot, printer.Id).GetProperty("printTimeSeconds").GetInt64());
    }

    [Fact]
    public async Task Snapshot_PrintTimeSeconds_IsZeroForAPrinterWithNoPrints()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Idle");

        var snapshot = await SnapshotAsync(user);

        Assert.Equal(0, Printer(snapshot, printer.Id).GetProperty("printTimeSeconds").GetInt64());
    }

    [Fact]
    public async Task Snapshot_LastMaintenance_IsNewestDone()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var other = await AddPrinterAsync(user, "Other");
        await AddMaintenanceAsync(user, printer, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), done: true, "Lubrication", "January");
        var newest = await AddMaintenanceAsync(user, printer, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), done: true, "Belts", "February");
        await AddMaintenanceAsync(user, printer, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), done: false, "Nozzle", "March");

        var snapshot = await SnapshotAsync(user);

        var last = Printer(snapshot, printer.Id).GetProperty("lastMaintenance");
        Assert.Equal(newest.Id, last.GetProperty("id").GetGuid());
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), last.GetProperty("date").GetDateTimeOffset());
        Assert.Equal("Belts", last.GetProperty("category").GetString());
        Assert.Equal("February", last.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, Printer(snapshot, other.Id).GetProperty("lastMaintenance").ValueKind);
    }

    [Fact]
    public async Task Snapshot_OpenMaintenance_CountsNotDone()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var none = await AddPrinterAsync(user, "None");
        await AddMaintenanceAsync(user, printer, new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), done: false);
        await AddMaintenanceAsync(user, printer, new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), done: false);
        await AddMaintenanceAsync(user, printer, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), done: true);

        var snapshot = await SnapshotAsync(user);

        var open = Printer(snapshot, printer.Id).GetProperty("openMaintenance");
        Assert.Equal(2, open.GetProperty("count").GetInt32());
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), open.GetProperty("nextDate").GetDateTimeOffset());
        var empty = Printer(snapshot, none.Id).GetProperty("openMaintenance");
        Assert.Equal(0, empty.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextDate").ValueKind);
    }

    [Fact]
    public async Task Snapshot_ETag_ChangesWhenMaintenanceAdded()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var first = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct);
        var etag = first.Headers.ETag!.ToString();

        var post = factory.Get(user, "/api/PrinterMaintenance");
        post.Method = HttpMethod.Post;
        post.Content = JsonContent.Create(new { printerId = printer.Id, done = false, date = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), category = "Nozzle" });
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(post, Ct)).StatusCode);

        var second = await _client.SendAsync(factory.Get(user, "/api/sync/snapshot", etag), Ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(etag, second.Headers.ETag!.ToString());
        var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(1, Printer(body, printer.Id).GetProperty("openMaintenance").GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData(PrintSource.SlicerPlugin, "slicer_plugin")]
    [InlineData(PrintSource.ApiKey, "api_key")]
    [InlineData(PrintSource.OctoPrint, "octoprint")]
    [InlineData(PrintSource.Unknown, "unknown")]
    public async Task Snapshot_Source_IsSnakeCase(PrintSource source, string expected)
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        await AddPrintAsync(user, printer, Print.PrintStatus.Printing, SyncTestFactory.Start.AddMinutes(-5), source: source);
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, SyncTestFactory.Start.AddHours(-5), source: source);

        var p = Printer(await SnapshotAsync(user), printer.Id);

        Assert.Equal(expected, p.GetProperty("currentPrint").GetProperty("source").GetString());
        Assert.Equal(expected, p.GetProperty("lastFinishedPrint").GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("https://www.example.test/", true)]
    [InlineData("", false)]
    public async Task Snapshot_Url_UsesWebBaseUrl(string webBase, bool expectUrl)
    {
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.PostConfigure<PrintLogApi.Email.EmailOptions>(o => o.WebBaseUrl = webBase)));
        var client = host.CreateClient();
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var current = await AddPrintAsync(user, printer, Print.PrintStatus.Printing, SyncTestFactory.Start.AddMinutes(-5));
        var finished = await AddPrintAsync(user, printer, Print.PrintStatus.Success, SyncTestFactory.Start.AddHours(-5));

        var response = await client.SendAsync(factory.Get(user, "/api/sync/snapshot"), Ct);
        var p = Printer(JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement, printer.Id);

        var cur = p.GetProperty("currentPrint").GetProperty("url");
        var fin = p.GetProperty("lastFinishedPrint").GetProperty("url");
        if (expectUrl)
        {
            Assert.Equal($"https://www.example.test/prints/{current.Id}", cur.GetString());
            Assert.Equal($"https://www.example.test/prints/{finished.Id}", fin.GetString());
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, cur.ValueKind);
            Assert.Equal(JsonValueKind.Null, fin.ValueKind);
        }
    }

    [Fact]
    public async Task Snapshot_CurrentPrintFilament_SlotOrderGrams()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        var spool = await AddSpoolAsync(user, "Black", 1_000_000, 1);
        await AddPrintAsync(user, printer, Print.PrintStatus.Printing, SyncTestFactory.Start.AddMinutes(-5),
            estimates: [(2, spool, 12_345), (null, null, 500), (1, null, null)]);

        var f = Printer(await SnapshotAsync(user), printer.Id).GetProperty("currentPrint").GetProperty("filament").EnumerateArray().ToList();

        Assert.Equal(3, f.Count);
        Assert.Equal(1, f[0].GetProperty("slot").GetInt32());
        Assert.Equal(JsonValueKind.Null, f[0].GetProperty("spoolId").ValueKind);
        Assert.Equal(JsonValueKind.Null, f[0].GetProperty("estimatedGrams").ValueKind);
        Assert.Equal(2, f[1].GetProperty("slot").GetInt32());
        Assert.Equal(spool.Id, f[1].GetProperty("spoolId").GetGuid());
        Assert.Equal(12.3, f[1].GetProperty("estimatedGrams").GetDouble());
        Assert.Equal(JsonValueKind.Null, f[2].GetProperty("slot").ValueKind);
        Assert.Equal(0.5, f[2].GetProperty("estimatedGrams").GetDouble());
    }

    [Fact]
    public async Task Snapshot_CurrentPrintFilament_IsEmptyWithoutRows()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        await AddPrintAsync(user, printer, Print.PrintStatus.Printing, SyncTestFactory.Start.AddMinutes(-5));

        var f = Printer(await SnapshotAsync(user), printer.Id).GetProperty("currentPrint").GetProperty("filament");

        Assert.Equal(0, f.GetArrayLength());
    }

    [Fact]
    public async Task Snapshot_Account_SuccessAndFailedCounts()
    {
        var user = await factory.CreateUserAsync();
        var printer = await AddPrinterAsync(user, "Voron");
        factory.Clock.SetUtcNow(SyncTestFactory.Start);
        var day = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, day);
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, day.AddHours(1));
        await AddPrintAsync(user, printer, Print.PrintStatus.Failed, day.AddHours(2));
        await AddPrintAsync(user, printer, Print.PrintStatus.Cancelled, day.AddHours(3));
        await AddPrintAsync(user, printer, Print.PrintStatus.PartialSuccess, day.AddHours(4));
        await AddPrintAsync(user, printer, Print.PrintStatus.Success, day.AddMonths(-1));

        var a = (await SnapshotAsync(user)).GetProperty("account");

        Assert.Equal(2, a.GetProperty("successCount").GetInt32());
        Assert.Equal(1, a.GetProperty("failedCount").GetInt32());
        Assert.Equal(5, a.GetProperty("printCount").GetInt32());
    }
}
