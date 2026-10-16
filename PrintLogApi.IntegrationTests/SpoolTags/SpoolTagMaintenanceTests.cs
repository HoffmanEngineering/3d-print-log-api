using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

[Collection("SpoolTagCatalogImport")]
public class SpoolTagMaintenanceTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static TagReadFailure Failure(string uid, DateTime createdAt) => new()
    {
        Uid = uid,
        CreatedAt = createdAt,
        MemoryKind = "type2",
        Data = [1, 2, 3],
        Error = "test",
    };

    private static async Task SeedFailures(CatalogTestHost host)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.TagReadFailures.AddRange(
            Failure("OLD", Now.AddDays(-91)),
            Failure("EDGE", Now.AddDays(-89)),
            Failure("NEW", Now.AddDays(-1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string[]> Uids(CatalogTestHost host)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>()
            .TagReadFailures.OrderBy(f => f.Uid).Select(f => f.Uid).ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> CatalogCount(CatalogTestHost host)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>()
            .FilamentCatalogEntries.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunOnce_DeletesFailuresOlderThanNinetyDays_AndKeepsNewerOnes()
    {
        await using var host = new CatalogTestHost(importEnabled: false);
        await SeedFailures(host);

        await host.Maintenance().RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(["EDGE", "NEW"], await Uids(host));
    }

    [Fact]
    public async Task RunOnce_WithImportDisabled_MakesNoHttpCall_ButStillPurges()
    {
        await using var host = new CatalogTestHost(importEnabled: false);
        await SeedFailures(host);

        await host.Maintenance().RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(0, host.Stub.RequestCount);
        Assert.Equal(2, (await Uids(host)).Length);
    }

    [Fact]
    public async Task RunOnce_WithImportEnabled_ImportsTheCatalog()
    {
        await using var host = new CatalogTestHost();
        host.Stub.Serve(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "spool-tags", "spoolmandb-sample.json")));

        await host.Maintenance().RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(6, await CatalogCount(host));
    }

    [Fact]
    public async Task RunOnce_WhenTheImportFails_StillPurges()
    {
        await using var host = new CatalogTestHost();
        host.Stub.ServeStatus(System.Net.HttpStatusCode.InternalServerError);
        await SeedFailures(host);

        await host.Maintenance().RunOnceAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal(1, host.Stub.RequestCount);
        Assert.Equal(["EDGE", "NEW"], await Uids(host));
    }
}
