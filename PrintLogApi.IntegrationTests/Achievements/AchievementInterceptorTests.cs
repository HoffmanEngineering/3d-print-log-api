using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.IntegrationTests.Support;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.IntegrationTests.Achievements.AchievementMetricTestKit;

namespace PrintLogApi.IntegrationTests.Achievements;

/// <summary>Records every pass instead of running it, so tests can assert on the triggers.</summary>
public sealed class RecordingPassRunner : IAchievementPassRunner
{
    public ConcurrentQueue<PendingWork> Runs { get; } = new();

    public Exception? ThrowOnRun { get; set; }

    public Task RunAsync(PendingWork work, CancellationToken ct)
    {
        Runs.Enqueue(work);
        return ThrowOnRun is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }

    /// <summary>The flags every recorded pass carried for <paramref name="userId"/>, in order.</summary>
    public List<AchievementTrigger> For(long userId) =>
        Runs.Where(w => w.ByUser.ContainsKey(userId)).Select(w => w.ByUser[userId]).ToList();
}

/// <summary>The normal test host, with the pass runner swapped for a recorder.</summary>
public sealed class RecordingRunnerFactory : CustomWebApplicationFactory
{
    public RecordingPassRunner Recorder { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddSingleton<IAchievementPassRunner>(Recorder));
    }
}

/// <summary>What the save hooks record, and when they hand it to the runner.</summary>
public class AchievementTriggerRecordingTests : IClassFixture<RecordingRunnerFactory>
{
    private readonly RecordingRunnerFactory _factory;

    public AchievementTriggerRecordingTests(RecordingRunnerFactory factory) => _factory = factory;

    private RecordingPassRunner Recorder => _factory.Recorder;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(IServiceScope Scope, PrintLogContext Db, User User)> ArrangeAsync()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return (scope, db, await AchievementTestData.CreateUserAsync(db));
    }

    private static PutPrintDetailDto PutFrom(Print p) => new()
    {
        Id = p.Id,
        Title = p.Title,
        StartDate = p.StartDate,
        PrinterId = p.PrinterId,
        Status = p.Status,
        ViewStatus = p.ViewStatus,
        PrintTimeInSeconds = p.PrintTimeInSeconds,
        EstimatedPrintTimeInSeconds = p.EstimatedPrintTimeInSeconds,
        FilamentUsage = p.FilamentUsage!.Select(f => new PutPrintFilamentSummaryDto
        {
            Id = f.Id,
            FilamentId = f.FilamentId,
            AmountMg = f.AmountMg,
            EstimatedAmountMg = f.EstimatedAmountMg,
        }).ToList(),
    };

    private static async Task<Print> PrintWithUsageAsync(PrintLogContext db, User user)
    {
        var spool = await AddFilamentAsync(db, user);
        var print = await AchievementTestData.AddPrintAsync(db, user);
        await AddUsageAsync(db, print, spool.Id, amountMg: 5000);
        return print;
    }

    [Fact]
    public async Task TitleOnlyEdit_ThroughUpdatePrint_RaisesNoPass()
    {
        long userId, printId;
        using (var seed = _factory.Services.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await AchievementTestData.CreateUserAsync(seedDb);
            userId = user.Id;
            printId = (await PrintWithUsageAsync(seedDb, user)).Id;
        }
        Recorder.Runs.Clear();

        using var scope = _factory.Services.CreateScope();
        var prints = scope.ServiceProvider.GetRequiredService<IPrintService>();
        var existing = (await prints.GetPrintById(printId))!;
        var dto = PutFrom(existing);
        dto.Title = "Renamed";
        await prints.UpdatePrint(printId, dto, userId);

        Assert.Empty(Recorder.For(userId));
        Assert.DoesNotContain(Recorder.Runs, w => w.PrintIdTriggers.ContainsKey(printId));
    }

    [Fact]
    public async Task StatusEdit_ThroughUpdatePrint_RaisesPrintUpdated()
    {
        long userId, printId;
        using (var seed = _factory.Services.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await AchievementTestData.CreateUserAsync(seedDb);
            userId = user.Id;
            printId = (await PrintWithUsageAsync(seedDb, user)).Id;
        }
        Recorder.Runs.Clear();

        using var scope = _factory.Services.CreateScope();
        var prints = scope.ServiceProvider.GetRequiredService<IPrintService>();
        var dto = PutFrom((await prints.GetPrintById(printId))!);
        dto.Status = Print.PrintStatus.Failed;
        await prints.UpdatePrint(printId, dto, userId);

        Assert.Equal([AchievementTrigger.PrintUpdated], Recorder.For(userId));
    }

    [Fact]
    public async Task UsageAmountEdit_ThroughUpdatePrint_RaisesPrintUpdatedForThePrint()
    {
        long userId, printId;
        using (var seed = _factory.Services.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await AchievementTestData.CreateUserAsync(seedDb);
            userId = user.Id;
            printId = (await PrintWithUsageAsync(seedDb, user)).Id;
        }
        Recorder.Runs.Clear();

        using var scope = _factory.Services.CreateScope();
        var prints = scope.ServiceProvider.GetRequiredService<IPrintService>();
        var dto = PutFrom((await prints.GetPrintById(printId))!);
        dto.FilamentUsage!.Single().AmountMg = 900_000;
        await prints.UpdatePrint(printId, dto, userId);

        var run = Assert.Single(Recorder.Runs, w => w.PrintIdTriggers.ContainsKey(printId));
        Assert.Equal(AchievementTrigger.PrintUpdated, run.PrintIdTriggers[printId]);
    }

    [Fact]
    public async Task TimeZoneSetting_RaisesTimeZoneChanged()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        Recorder.Runs.Clear();

        db.UserSettings.Add(new UserSetting { UserId = user.Id, UserSettingTypeId = 20, Value = "Europe/Paris", CreatedById = user.Id, UpdatedById = user.Id });
        await db.SaveChangesAsync(Ct);

        Assert.Equal([AchievementTrigger.TimeZoneChanged], Recorder.For(user.Id));
    }

    [Fact]
    public async Task ProfileEdit_RaisesProfileChanged_OtherUserEditsDoNot()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        Recorder.Runs.Clear();

        user.ViewStatus = User.ProfileViewStatus.Private;
        await db.SaveChangesAsync(Ct);
        Assert.Empty(Recorder.For(user.Id));

        user.Bio = "I print things.";
        await db.SaveChangesAsync(Ct);
        Assert.Equal([AchievementTrigger.ProfileChanged], Recorder.For(user.Id));
    }

    [Fact]
    public async Task Comment_TriggersByPrintId_ForTheOwnerToResolve()
    {
        var (scope, db, owner) = await ArrangeAsync();
        using var _ = scope;
        var commenter = await AchievementTestData.CreateUserAsync(db);
        var print = await AchievementTestData.AddPrintAsync(db, owner);
        Recorder.Runs.Clear();

        await AddCommentAsync(db, print, commenter.Id);

        var run = Assert.Single(Recorder.Runs, w => w.PrintIdTriggers.ContainsKey(print.Id));
        Assert.Equal(AchievementTrigger.CommentReceived, run.PrintIdTriggers[print.Id]);
    }

    [Fact]
    public async Task ExplicitTransaction_RunsAfterCommit_NotBefore()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var printer = await AchievementTestData.AddPrinterAsync(db, user);
        Recorder.Runs.Clear();

        await using (var tx = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Prints.Add(new Print { Title = "Tx", PrinterId = printer.Id, CreatedById = user.Id, UpdatedById = user.Id });
            await db.SaveChangesAsync(Ct);
            db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Tx", CreatedById = user.Id, UpdatedById = user.Id });
            await db.SaveChangesAsync(Ct);

            Assert.Empty(Recorder.For(user.Id));
            await tx.CommitAsync(Ct);
        }

        // Both saves' triggers, in one pass.
        Assert.Equal([AchievementTrigger.PrintAdded | AchievementTrigger.ProjectChanged], Recorder.For(user.Id));
    }

    [Fact]
    public async Task ExplicitTransaction_Rollback_RunsNothing()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var printer = await AchievementTestData.AddPrinterAsync(db, user);
        Recorder.Runs.Clear();

        await using (var tx = await db.Database.BeginTransactionAsync(Ct))
        {
            db.Prints.Add(new Print { Title = "Tx", PrinterId = printer.Id, CreatedById = user.Id, UpdatedById = user.Id });
            await db.SaveChangesAsync(Ct);
            await tx.RollbackAsync(Ct);
        }
        db.ChangeTracker.Clear();

        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "After", CreatedById = user.Id, UpdatedById = user.Id });
        await db.SaveChangesAsync(Ct);

        Assert.Equal([AchievementTrigger.ProjectChanged], Recorder.For(user.Id));
    }

    [Fact]
    public async Task FailedSave_DoesNotLeakTriggersIntoNextSave()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        Recorder.Runs.Clear();

        db.Prints.Add(new Print { Title = "Orphan", PrinterId = long.MaxValue, CreatedById = user.Id, UpdatedById = user.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        db.ChangeTracker.Clear();

        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Next", CreatedById = user.Id, UpdatedById = user.Id });
        await db.SaveChangesAsync(Ct);

        Assert.Equal([AchievementTrigger.ProjectChanged], Recorder.For(user.Id));
    }

    [Fact]
    public async Task RetriedTransaction_RunsExactlyOnceAfterCommittedAttempt()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var printer = await AchievementTestData.AddPrinterAsync(db, user);
        Recorder.Runs.Clear();

        var attempt = 0;
        var strategy = new TestRetryingExecutionStrategy(db);
        await strategy.ExecuteAsync(async () =>
        {
            attempt++;
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            if (attempt == 1)
            {
                // Saved inside the transaction, then the attempt dies before committing.
                db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Lost", CreatedById = user.Id, UpdatedById = user.Id });
                await db.SaveChangesAsync(Ct);
                throw new TransientTestException();
            }

            db.Prints.Add(new Print { Title = "Kept", PrinterId = printer.Id, CreatedById = user.Id, UpdatedById = user.Id });
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        });

        Assert.Equal(2, attempt);
        Assert.Equal([AchievementTrigger.PrintAdded], Recorder.For(user.Id));
    }

    [Fact]
    public async Task RunnerFailure_DoesNotFailRequest()
    {
        Recorder.ThrowOnRun = new InvalidOperationException("Simulated pass failure.");
        try
        {
            var client = _factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = JsonContent.Create(new AddPrintDTO
            {
                Title = "Still saves",
                PrinterId = IntegrationTestSeeder.TestPrinterId,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                FilamentUsage = [],
            });

            var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            Recorder.ThrowOnRun = null;
        }
    }
}

/// <summary>End-to-end: saves through the real interceptors and the real runner and evaluator.</summary>
public class AchievementInterceptorTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementInterceptorTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A user already caught up to the catalog, so only triggered passes grant.</summary>
    private async Task<(IServiceScope Scope, PrintLogContext Db, User User)> ArrangeAsync()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await AchievementTestData.CreateUserAsync(db);
        user.AchievementCatalogVersion = AchievementCatalog.Version;
        await db.SaveChangesAsync(Ct);
        return (scope, db, user);
    }

    private async Task<bool> HoldsAsync(long userId, string key, int tier = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.UserAchievements.AnyAsync(a => a.UserId == userId && a.AchievementKey == key && a.Tier == tier, Ct);
    }

    [Fact]
    public async Task AddPrint_NoTransaction_GrantsFirstPrint()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;

        await AchievementTestData.AddPrintAsync(db, user);

        Assert.True(await HoldsAsync(user.Id, "first-print"));
        Assert.True(await HoldsAsync(user.Id, "first-printer"));
    }

    [Fact]
    public async Task EstimateOnlyEdit_QualifiesForHours()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var print = await AchievementTestData.AddPrintAsync(db, user, p => p.StartDate = null);
        Assert.False(await HoldsAsync(user.Id, "print-hours"));

        using var editScope = _factory.Services.CreateScope();
        var prints = editScope.ServiceProvider.GetRequiredService<IPrintService>();
        var existing = (await prints.GetPrintById(print.Id))!;
        await prints.UpdatePrint(print.Id, new PutPrintDetailDto
        {
            Id = existing.Id,
            Title = existing.Title,
            PrinterId = existing.PrinterId,
            Status = existing.Status,
            ViewStatus = existing.ViewStatus,
            EstimatedPrintTimeInSeconds = 24 * 3600,
            FilamentUsage = [],
        }, user.Id);

        Assert.True(await HoldsAsync(user.Id, "print-hours"));
    }

    [Fact]
    public async Task PrintFilamentAmountEdit_QualifiesForMaterialUsed()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var spool = await AddFilamentAsync(db, user);
        var print = await AchievementTestData.AddPrintAsync(db, user);
        await AddUsageAsync(db, print, spool.Id, amountMg: 5000);
        Assert.False(await HoldsAsync(user.Id, "material-used"));

        var usage = await db.PrintFilament.SingleAsync(pf => pf.PrintId == print.Id, Ct);
        usage.AmountMg = 1_000_000;
        await db.SaveChangesAsync(Ct);

        Assert.True(await HoldsAsync(user.Id, "material-used"));
    }

    [Fact]
    public async Task FilamentMaterialTypeEdit_QualifiesForMaterialTypes()
    {
        var (scope, db, user) = await ArrangeAsync();
        using var _ = scope;
        var a = await AddFilamentAsync(db, user, "PLA");
        var b = await AddFilamentAsync(db, user, "PLA");
        var print = await AchievementTestData.AddPrintAsync(db, user);
        await AddUsageAsync(db, print, a.Id);
        await AddUsageAsync(db, print, b.Id);
        Assert.False(await HoldsAsync(user.Id, "material-types"));

        b.MaterialType = "PETG";
        await db.SaveChangesAsync(Ct);

        Assert.True(await HoldsAsync(user.Id, "material-types"));
    }

    [Fact]
    public async Task Comment_TriggersPrintOwner_NotCommenter()
    {
        var (scope, db, owner) = await ArrangeAsync();
        using var _ = scope;
        var commenter = await AchievementTestData.CreateUserAsync(db);
        commenter.AchievementCatalogVersion = AchievementCatalog.Version;
        await db.SaveChangesAsync(Ct);
        var print = await AchievementTestData.AddPrintAsync(db, owner);

        await AddCommentAsync(db, print, commenter.Id);

        Assert.True(await HoldsAsync(owner.Id, "comments-received"));
        Assert.False(await HoldsAsync(commenter.Id, "comments-received"));
    }

    [Fact]
    public async Task EvaluatorContext_IsSuppressed()
    {
        var spy = new ContextCapturingEvaluator();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddScoped<IAchievementEvaluator>(sp =>
        {
            spy.Captured.Enqueue(sp.GetRequiredService<PrintLogContext>());
            return spy;
        })));
        var tracker = factory.Services.GetRequiredService<AchievementTriggerTracker>();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await AchievementTestData.CreateUserAsync(db);

        await AchievementTestData.AddPrintAsync(db, user);

        Assert.NotEmpty(spy.Captured);
        Assert.All(spy.Captured, ctx => Assert.True(tracker.IsSuppressed(ctx)));
        Assert.False(tracker.IsSuppressed(db));
    }

    private sealed class ContextCapturingEvaluator : IAchievementEvaluator
    {
        public ConcurrentQueue<PrintLogContext> Captured { get; } = new();

        public Task<EvaluationResult> EvaluateAsync(long userId, AchievementTrigger triggers, EvaluationMode mode, CancellationToken ct = default) =>
            Task.FromResult(EvaluationResult.Empty);
    }
}
