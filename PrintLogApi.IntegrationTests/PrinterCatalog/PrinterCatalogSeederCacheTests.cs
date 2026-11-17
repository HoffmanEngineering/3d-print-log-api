using PrintLogApi.PrinterCatalog.Generation;
using Xunit;
using static PrintLogApi.IntegrationTests.PrinterCatalog.PrinterCatalogTestData;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// The cached printer summaries carry catalogModelId, so printers moved off a merged id must
/// invalidate their owners' caches (and nobody else's).
/// </summary>
public class PrinterCatalogSeederCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CatalogFile V1() => File(Model("old", "Acme", "Old"), Model("new", "Acme", "New"), Model("other", "Acme", "Other"));

    private static CatalogFile V2() => File(Model("new", "Acme", "New"), Model("other", "Acme", "Other")) with
    {
        Version = "v2",
        Reserved = [new ReservedId { Id = "old", MergedInto = "new" }],
    };

    [Fact]
    public async Task RepointedPrinters_InvalidateTheirOwnersCaches_AndNoOneElses()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.Seeder(V1).RunOnceAsync(Ct);
        var a = await AddUserAsync(host);
        var b = await AddUserAsync(host);
        var c = await AddUserAsync(host);
        var pa = await AddPrinterAsync(host, a.Id, "old", null);
        var pb = await AddPrinterAsync(host, b.Id, "old", null);
        var pb2 = await AddPrinterAsync(host, b.Id, "old", null);
        var pc = await AddPrinterAsync(host, c.Id, "other", null);
        var before = new[] { a.Id, b.Id, c.Id }.Select(host.CacheVersions.GetUserCacheVersion).ToArray();

        await host.Seeder(V2).RunOnceAsync(Ct);

        Assert.Equal(["new", "new", "new", "other"],
            [.. new[] { pa, pb, pb2, pc }.Select(async id => (await GetPrinterAsync(host, id)).CatalogModelId).Select(t => t.Result)]);
        Assert.NotEqual(before[0], host.CacheVersions.GetUserCacheVersion(a.Id));
        Assert.NotEqual(before[1], host.CacheVersions.GetUserCacheVersion(b.Id));
        Assert.Equal(before[2], host.CacheVersions.GetUserCacheVersion(c.Id));
    }

    [Fact]
    public async Task AnUnchangedSeed_InvalidatesNothing()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.Seeder(V2).RunOnceAsync(Ct);
        var a = await AddUserAsync(host);
        await AddPrinterAsync(host, a.Id, "new", null);
        var before = host.CacheVersions.GetUserCacheVersion(a.Id);

        await host.Seeder(V2).RunOnceAsync(Ct);

        Assert.Equal(before, host.CacheVersions.GetUserCacheVersion(a.Id));
    }
}
