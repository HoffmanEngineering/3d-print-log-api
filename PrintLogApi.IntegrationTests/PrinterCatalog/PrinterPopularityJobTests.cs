using Xunit;
using static PrintLogApi.IntegrationTests.PrinterCatalog.PrinterCatalogTestData;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

public class PrinterPopularityJobTests
{
    private static readonly DateTimeOffset Now = PrinterCatalogTestHost.Start;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<PrinterCatalogTestHost> HostAsync()
    {
        var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File(Model("a", "Acme", "A"), Model("b", "Acme", "B")));
        return host;
    }

    private static async Task<int> OwnersAsync(PrinterCatalogTestHost host, string id) =>
        (await host.RowsAsync())[id].OwnerCount;

    [Fact]
    public async Task OldPrinterWithoutPrints_CountsZero()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, "a", Now.AddMonths(-13));

        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(0, await OwnersAsync(host, "a"));
    }

    [Fact]
    public async Task OldPrinterWithARecentPrint_CountsOne()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, "a", Now.AddMonths(-13), printStarts: Now.AddMonths(-1));

        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(1, await OwnersAsync(host, "a"));
    }

    [Fact]
    public async Task PrinterWithPrintsOnlyOutsideTheWindow_CountsZero()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, "a", null, printStarts: Now.AddMonths(-14));

        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(0, await OwnersAsync(host, "a"));
    }

    [Fact]
    public async Task RecentlyAddedPrinter_CountsOne()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, "a", Now.AddMonths(-1));

        var updated = await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(1, updated);
        Assert.Equal(1, await OwnersAsync(host, "a"));
    }

    [Fact]
    public async Task TwoPrintersOfOneUser_CountOnce_AndInactiveOnesNotAtAll()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        var other = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, "a", Now.AddDays(-1));
        await AddPrinterAsync(host, user.Id, "a", Now.AddDays(-2), printStarts: Now.AddDays(-3));
        await AddPrinterAsync(host, other.Id, "b", Now.AddDays(-1), active: false);

        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(1, await OwnersAsync(host, "a"));
        Assert.Equal(0, await OwnersAsync(host, "b"));
    }

    [Fact]
    public async Task PrinterWithoutACatalogModel_IsIgnored()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, null, Now.AddDays(-1));

        Assert.Equal(0, await host.Popularity.RunOnceAsync(Now, Ct));
    }

    [Fact]
    public async Task ModelThatNoLongerQualifies_DropsToZero_AndNothingElseIsWritten()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, "a", Now.AddDays(-1));
        await host.Popularity.RunOnceAsync(Now, Ct);
        var before = (await host.RowsAsync())["a"];
        Assert.Equal(1, before.OwnerCount);

        var updated = await host.Popularity.RunOnceAsync(Now.AddYears(2), Ct);

        Assert.Equal(1, updated);
        var after = (await host.RowsAsync())["a"];
        Assert.Equal(0, after.OwnerCount);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        var expected = Columns(before);
        expected[nameof(after.OwnerCount)] = 0;
        Assert.Equal(expected, Columns(after));
    }

    [Fact]
    public async Task UnchangedCounts_UpdateNothing()
    {
        await using var host = await HostAsync();
        var user = await AddUserAsync(host);
        await AddPrinterAsync(host, user.Id, "a", Now.AddDays(-1));
        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(0, await host.Popularity.RunOnceAsync(Now, Ct));
    }

    [Fact]
    public async Task Popular_FallsBackToFeaturedBelowTheThreshold_AndRanksByCountAtIt()
    {
        await using var host = new PrinterCatalogTestHost();
        await host.SeedAsync(File(Model("a", "Acme", "A", featured: 1), Model("b", "Acme", "B")));

        for (var i = 0; i < 4; i++)
        {
            await AddPrinterAsync(host, (await AddUserAsync(host)).Id, "b", Now.AddDays(-1));
        }
        await host.Popularity.RunOnceAsync(Now, Ct);
        Assert.Equal("a", host.Index.Popular(5, minOwners: 5)[0].Id);

        await AddPrinterAsync(host, (await AddUserAsync(host)).Id, "b", Now.AddDays(-1));
        await host.Popularity.RunOnceAsync(Now, Ct);
        Assert.Equal(["b", "a"], host.Index.Popular(5, minOwners: 5).Select(m => m.Id));
    }

    [Fact]
    public async Task Run_OnAnEmptyTable_LeavesAnUnbuiltIndexNotReady()
    {
        // A failed first seed leaves the table empty; the daily tick must not mark that ready.
        await using var host = new PrinterCatalogTestHost();

        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.False(host.Index.IsReady);
    }

    [Fact]
    public async Task Run_RebuildsTheIndexWithTheNewCounts()
    {
        await using var host = await HostAsync();
        await AddPrinterAsync(host, (await AddUserAsync(host)).Id, "a", Now.AddDays(-1));

        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(1, host.Index.Get("a")!.OwnerCount);
    }

    [Fact]
    public async Task TheWindowIsTheConfiguredNumberOfDays()
    {
        await using var host = await HostAsync();
        await AddPrinterAsync(host, (await AddUserAsync(host)).Id, "a", Now.AddDays(-100));

        await host.Popularity.RunOnceAsync(Now, Ct);
        Assert.Equal(1, await OwnersAsync(host, "a"));

        // 365 days after the printer's day 100, it is 465 days old and out of the window.
        Assert.Equal(1, await host.Popularity.RunOnceAsync(Now.AddDays(266), Ct));
        Assert.Equal(0, await OwnersAsync(host, "a"));
    }

    [Fact]
    public async Task ACreatedAtOrPrintExactlyAtTheCutoff_Counts()
    {
        await using var host = await HostAsync();
        var cutoff = Now.AddDays(-365);
        await AddPrinterAsync(host, (await AddUserAsync(host)).Id, "a", cutoff);
        await AddPrinterAsync(host, (await AddUserAsync(host)).Id, "b", Now.AddYears(-3), printStarts: cutoff);

        await host.Popularity.RunOnceAsync(Now, Ct);

        Assert.Equal(1, await OwnersAsync(host, "a"));
        Assert.Equal(1, await OwnersAsync(host, "b"));
    }
}
