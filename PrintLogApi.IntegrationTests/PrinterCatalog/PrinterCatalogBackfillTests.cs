using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;
using static PrintLogApi.IntegrationTests.PrinterCatalog.PrinterCatalogTestData;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

public class PrinterCatalogBackfillTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Seeds <paramref name="models"/> and builds the index the way startup does.</summary>
    private static async Task<PrinterCatalogTestHost> HostAsync(params CatalogModel[] models)
    {
        var host = new PrinterCatalogTestHost();
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        await PrinterCatalogSeeder.SeedAsync(db, File(models), host.Time.GetUtcNow(), Ct);
        host.Index.Rebuild(await db.PrinterModels.AsNoTracking().ToListAsync(Ct));
        return host;
    }

    private static CatalogModel P1S() => Model("bambu-lab-p1s", "Bambu Lab", "P1S", ["Bambu Lab P1S Combo"]);

    [Fact]
    public async Task MakeAndModel_LinkToTheCatalogModel()
    {
        await using var host = await HostAsync(P1S());
        var user = await AddUserAsync(host);
        var id = await AddPrinterAsync(host, user.Id, null, null, make: "Bambu Lab", model: "P1S");

        var result = await host.Backfill.RunOnceAsync(Ct);

        Assert.Equal(new BackfillResult(1, 0, 0), result);
        Assert.Equal("bambu-lab-p1s", (await GetPrinterAsync(host, id)).CatalogModelId);
    }

    [Fact]
    public async Task AnAliasMatch_Links()
    {
        await using var host = await HostAsync(P1S());
        var id = await AddPrinterAsync(host, (await AddUserAsync(host)).Id, null, null, make: "Bambu Lab", model: "P1S Combo");

        var result = await host.Backfill.RunOnceAsync(Ct);

        Assert.Equal(1, result.Linked);
        Assert.Equal("bambu-lab-p1s", (await GetPrinterAsync(host, id)).CatalogModelId);
    }

    [Fact]
    public async Task AmbiguousName_StaysNull()
    {
        await using var host = await HostAsync(
            Model("acme-x1", "Acme", "X1", ["Twin"]),
            Model("acme-x2", "Acme", "X2", ["Twin"]));
        var id = await AddPrinterAsync(host, (await AddUserAsync(host)).Id, null, null, make: "Acme", model: "Twin");

        var result = await host.Backfill.RunOnceAsync(Ct);

        Assert.Equal(0, result.Linked);
        Assert.Equal(1, result.Ambiguous + result.Unmatched);
        Assert.Null((await GetPrinterAsync(host, id)).CatalogModelId);
    }

    [Fact]
    public async Task FuzzyMatch_StaysNull_AndCountsAsAmbiguous()
    {
        await using var host = await HostAsync(P1S(), Model("acme-other", "Acme", "Other"));
        var id = await AddPrinterAsync(host, (await AddUserAsync(host)).Id, null, null, make: "Bambu", model: "P1");

        var result = await host.Backfill.RunOnceAsync(Ct);

        Assert.Equal(new BackfillResult(0, 1, 0), result);
        Assert.Null((await GetPrinterAsync(host, id)).CatalogModelId);
    }

    [Fact]
    public async Task UnknownName_CountsAsUnmatched()
    {
        await using var host = await HostAsync(P1S());
        await AddPrinterAsync(host, (await AddUserAsync(host)).Id, null, null, make: "Zzyzx", model: "Quark");
        await AddPrinterAsync(host, (await AddUserAsync(host)).Id, null, null);

        Assert.Equal(new BackfillResult(0, 0, 2), await host.Backfill.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task AlreadyLinkedPrinter_IsUntouched()
    {
        await using var host = await HostAsync(P1S(), Model("acme-a", "Acme", "A"));
        var id = await AddPrinterAsync(host, (await AddUserAsync(host)).Id, "acme-a", null, make: "Bambu Lab", model: "P1S");

        var result = await host.Backfill.RunOnceAsync(Ct);

        Assert.Equal(new BackfillResult(0, 0, 0), result);
        Assert.Equal("acme-a", (await GetPrinterAsync(host, id)).CatalogModelId);
    }

    [Fact]
    public async Task OnlyTheLinkChanges_EveryOtherColumnIsTheSame()
    {
        await using var host = await HostAsync(P1S());
        var id = await AddPrinterAsync(host, (await AddUserAsync(host)).Id, null, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            make: "Bambu Lab", model: "P1S");
        var before = await GetPrinterAsync(host, id);

        await host.Backfill.RunOnceAsync(Ct);

        var after = await GetPrinterAsync(host, id);
        Assert.Equal("bambu-lab-p1s", after.CatalogModelId);
        var expected = Columns(before);
        expected[nameof(after.CatalogModelId)] = "bambu-lab-p1s";
        Assert.Equal(expected, Columns(after));
    }

    [Fact]
    public async Task NeverLinksToARetiredOrHiddenModel()
    {
        await using var host = await HostAsync(P1S());
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.PrinterModels.ExecuteUpdateAsync(s => s.SetProperty(m => m.Retired, true), Ct);
            host.Index.Rebuild(await db.PrinterModels.AsNoTracking().ToListAsync(Ct));
        }
        var id = await AddPrinterAsync(host, (await AddUserAsync(host)).Id, null, null, make: "Bambu Lab", model: "P1S");

        Assert.Equal(0, (await host.Backfill.RunOnceAsync(Ct)).Linked);
        Assert.Null((await GetPrinterAsync(host, id)).CatalogModelId);

        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.PrinterModels.ExecuteUpdateAsync(s => s.SetProperty(m => m.Retired, false).SetProperty(m => m.Hidden, true), Ct);
            host.Index.Rebuild(await db.PrinterModels.AsNoTracking().ToListAsync(Ct));
        }

        Assert.Equal(0, (await host.Backfill.RunOnceAsync(Ct)).Linked);
        Assert.Null((await GetPrinterAsync(host, id)).CatalogModelId);
    }

    [Fact]
    public async Task ProcessesMoreThanOneBatch()
    {
        await using var host = await HostAsync(P1S());
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = new User { OAuthUserId = $"catalog-{Guid.NewGuid():N}" };
            for (var i = 0; i < 1100; i++)
            {
                var match = i % 2 == 0;
                db.Printers.Add(new Printer
                {
                    User = user,
                    IsActive = true,
                    Make = match ? "Bambu Lab" : "Nope",
                    Model = match ? "P1S" : "None",
                });
            }
            await db.SaveChangesAsync(Ct);
        }

        var result = await host.Backfill.RunOnceAsync(Ct);

        Assert.Equal(new BackfillResult(550, 0, 550), result);
        await using var check = host.CreateScope();
        Assert.Equal(550, await check.ServiceProvider.GetRequiredService<PrintLogContext>().Printers
            .CountAsync(p => p.CatalogModelId == "bambu-lab-p1s", Ct));
    }

    [Fact]
    public async Task LinkingBumpsTheUsersCacheVersion_ButLeavesOthersAlone()
    {
        await using var host = await HostAsync(P1S());
        var linked = await AddUserAsync(host);
        var untouched = await AddUserAsync(host);
        await AddPrinterAsync(host, linked.Id, null, null, make: "Bambu Lab", model: "P1S");
        await AddPrinterAsync(host, untouched.Id, null, null, make: "Zzyzx", model: "Quark");
        var linkedBefore = host.CacheVersions.GetUserCacheVersion(linked.Id);
        var untouchedBefore = host.CacheVersions.GetUserCacheVersion(untouched.Id);

        await host.Backfill.RunOnceAsync(Ct);

        Assert.NotEqual(linkedBefore, host.CacheVersions.GetUserCacheVersion(linked.Id));
        Assert.Equal(untouchedBefore, host.CacheVersions.GetUserCacheVersion(untouched.Id));
    }

    [Fact]
    public async Task SeederHook_RunsBackfillThenPopularity_OnlyAfterASeedThatChangedSomething()
    {
        await using var host = new PrinterCatalogTestHost();
        var user = await AddUserAsync(host);
        var id = await AddPrinterAsync(host, user.Id, null, host.Time.GetUtcNow().AddDays(-1), make: "Bambu Lab", model: "P1S");
        var file = File(P1S());

        await host.Seeder(() => file).RunOnceAsync(Ct);

        Assert.Equal("bambu-lab-p1s", (await GetPrinterAsync(host, id)).CatalogModelId);
        Assert.Equal(1, (await host.RowsAsync())["bambu-lab-p1s"].OwnerCount);

        // The same version seeds nothing, so a printer unlinked in between stays unlinked.
        await using (var scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Printers
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CatalogModelId, (string?)null), Ct);
        }
        await host.Seeder(() => file).RunOnceAsync(Ct);
        Assert.Null((await GetPrinterAsync(host, id)).CatalogModelId);
    }
}
