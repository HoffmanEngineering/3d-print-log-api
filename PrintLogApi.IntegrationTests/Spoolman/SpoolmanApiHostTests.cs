using System.Net;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using PrintLogApi.Services.Spoolman;
using Xunit;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>The Spoolman API's own rate-limit policy (#177): per connection, not per user.</summary>
public class SpoolmanRateLimitTests(SpoolmanRateLimitTests.Factory factory) : IClassFixture<SpoolmanRateLimitTests.Factory>
{
    public const int PerMinute = 2;

    public sealed class Factory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spoolman:RateLimitPerMinute"] = PerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EachConnection_HasItsOwnBudget()
    {
        var support = new SpoolmanTestSupport(factory.Services);
        var user = await support.CreateUserAsync(pro: true);
        var first = await support.ConnectAsync(user);
        var second = await support.ConnectAsync(user);
        var client = factory.CreateClient();

        async Task<HttpStatusCode> Health(string secret)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/spoolman/api/v1/health");
            request.Headers.Authorization = SpoolmanTestSupport.Basic(secret);
            return (await client.SendAsync(request, Ct)).StatusCode;
        }

        for (var i = 0; i < PerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await Health(first.Secret!));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, await Health(first.Secret!));
        // The same user's other printer is unaffected.
        Assert.Equal(HttpStatusCode.OK, await Health(second.Secret!));
    }
}

/// <summary>
/// The inventory query against real SQL Server, which SQLite cannot vouch for: the remaining and
/// used aggregates are correlated sums, the shape that raises error 8124 when written wrong.
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION is set; reuses the migrated scratch host.
/// </summary>
public class SpoolmanSqlServerTests(EmailPipelineSqlServerTests.SqlServerFactory factory)
    : IClassFixture<EmailPipelineSqlServerTests.SqlServerFactory>
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Inventory_ExecutesOnSqlServer()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");

        var support = new SpoolmanTestSupport(factory.Services);
        var user = await support.CreateUserAsync();
        var printerId = await support.CreatePrinterAsync(user);
        Filament filament;
        using (var scope = factory.Services.CreateScope())
        {
            filament = await scope.ServiceProvider.GetRequiredService<IFilamentService>().AddFilament(new AddFilamentDto
            {
                DisplayName = "SQL Server spool",
                Brand = "Prusament",
                MaterialType = "PLA",
                DiameterMm = 1.75,
                InitialNominalWeightMg = 1_000_000,
                MaterialDensityGramPerCubicCm = 1.24,
                IsActive = true,
            }, user.Id);

            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = new Print
            {
                Title = "Usage",
                StartDate = DateTimeOffset.UtcNow,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = printerId,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(Ct);
            db.PrintFilament.Add(new PrintFilament { PrintId = print.Id, FilamentId = filament.Id, LengthInM = 10, Source = PrintFilament.SourceMeasurement.Length });
            await db.SaveChangesAsync(Ct);
        }

        SpoolmanInventory inventory;
        using (var scope = factory.Services.CreateScope())
        {
            inventory = await scope.ServiceProvider.GetRequiredService<ISpoolmanInventoryService>().GetInventoryAsync(user.Id, Ct);
        }

        var spool = Assert.Single(inventory.Spools);
        Assert.Equal(filament.SpoolNumber, spool.Id);
        // 10 m of 1.75 mm PLA at 1.24 g/cm³ is about 29.8 g.
        Assert.InRange(spool.UsedWeight, 29, 31);
        Assert.InRange(spool.RemainingWeight!.Value, 969, 971);
        Assert.NotNull(spool.LastUsed);
    }
}
