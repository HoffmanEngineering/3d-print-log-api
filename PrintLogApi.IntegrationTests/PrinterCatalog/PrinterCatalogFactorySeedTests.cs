using Microsoft.EntityFrameworkCore;
using PrintLogApi.PrinterCatalog;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>Every test host starts with the real embedded catalog seeded and indexed.</summary>
public class PrinterCatalogFactorySeedTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task TheTestHost_SeedsTheEmbeddedCatalog_AndBuildsTheIndex()
    {
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        Assert.Equal(CustomWebApplicationFactory.PrinterCatalog.Models.Count,
            await db.PrinterModels.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("bambu-lab-p1s", factory.Services.GetRequiredService<IPrinterCatalogIndex>().Search("p1s", 1).Single().Id);
    }

    [Fact]
    public void TheTestHost_RemovesTheSeederHostedService() =>
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), s => s is PrinterCatalogSeeder);

    [Fact]
    public void AChildHost_GetsItsOwnIndex()
    {
        _ = factory.CreateClient();
        using var child = factory.WithWebHostBuilder(_ => { });

        Assert.Equal("bambu-lab-p1s", child.Services.GetRequiredService<IPrinterCatalogIndex>().Search("p1s", 1).Single().Id);
    }

    [Fact]
    public void AChildBuiltBeforeItsParent_LeavesBothIndexed()
    {
        using var parent = new CustomWebApplicationFactory();
        using var child = parent.WithWebHostBuilder(_ => { });

        Assert.Equal("bambu-lab-p1s", child.Services.GetRequiredService<IPrinterCatalogIndex>().Search("p1s", 1).Single().Id);
        Assert.Equal("bambu-lab-p1s", parent.Services.GetRequiredService<IPrinterCatalogIndex>().Search("p1s", 1).Single().Id);
    }
}
