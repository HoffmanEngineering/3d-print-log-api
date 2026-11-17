using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.IntegrationTests.Spoolman;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Models.SortEnums;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The queries #230 changed, executed against real SQL Server, which SQLite cannot vouch for:
/// the list (sorted by remaining), the MCP inventory, find_material's untracked-spool filter,
/// get_material and get_printer's loaded spools now all read <c>FilamentProfile.RemainingMg</c>,
/// a CASE around the correlated usage sums. A tracked and an untracked spool, both with usage.
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION is set; reuses the migrated scratch host.
/// </summary>
public class FilamentRemainingSqlServerTests(EmailPipelineSqlServerTests.SqlServerFactory factory)
    : IClassFixture<EmailPipelineSqlServerTests.SqlServerFactory>
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RemainingQueries_ExecuteOnSqlServer_AndReportNullForAnUntrackedSpool()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");

        var support = new SpoolmanTestSupport(factory.Services);
        var user = await support.CreateUserAsync();
        var printerId = await support.CreatePrinterAsync(user);
        Filament tracked, untracked;
        using (var scope = factory.Services.CreateScope())
        {
            var filaments = scope.ServiceProvider.GetRequiredService<IFilamentService>();
            tracked = await filaments.AddFilament(Spool("Tracked PLA", 1_000_000), user.Id);
            untracked = await filaments.AddFilament(Spool("Untracked PLA", null), user.Id);

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
            db.PrintFilament.AddRange(
                new PrintFilament { PrintId = print.Id, FilamentId = tracked.Id, AmountMg = 100_000, Source = PrintFilament.SourceMeasurement.Weight },
                new PrintFilament { PrintId = print.Id, FilamentId = untracked.Id, LengthInM = 10, Source = PrintFilament.SourceMeasurement.Length });
            db.PrinterFilament.AddRange(
                new PrinterFilament { PrinterId = printerId, FilamentId = tracked.Id, LoadedDateTime = DateTimeOffset.UtcNow, Slot = 0 },
                new PrinterFilament { PrinterId = printerId, FilamentId = untracked.Id, LoadedDateTime = DateTimeOffset.UtcNow, Slot = 1 });
            await db.SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var filaments = scope.ServiceProvider.GetRequiredService<IFilamentService>();

            foreach (var direction in new[] { SortDirection.Asc, SortDirection.Desc })
            {
                var list = await filaments.GetFilamentSummaryForUser(
                    user.Id, direction, FilamentSummarySortColumn.FilamentRemaining, 1, 25,
                    null, null, null, true, false, false);
                Assert.Equal(900_000, list.Items.Single(f => f.Id == tracked.Id).FilamentRemaining);
                Assert.Null(list.Items.Single(f => f.Id == untracked.Id).FilamentRemaining);
            }

            var inventory = await filaments.GetMaterialInventoryForMcp(user.Id, 1, 25, null, null, true, Ct);
            Assert.Equal(900.0, inventory.Items.Single(i => i.Id == tracked.Id).RemainingGrams);
            Assert.Null(inventory.Items.Single(i => i.Id == untracked.Id).RemainingGrams);

            var found = await filaments.FindMaterialForMcp(user.Id, "PLA", null, 100, Ct);
            var spool = Assert.Single(Assert.Single(found.Groups).Spools);
            Assert.Equal(tracked.Id, spool.Id);
            Assert.Equal(900.0, spool.RemainingGrams);

            Assert.Null((await filaments.GetOwnMaterialDetailForMcp(user.Id, untracked.Id, Ct)).RemainingGrams);

            var printer = await scope.ServiceProvider.GetRequiredService<IPrinterService>().GetPrinterForMcp(user.Id, printerId, Ct);
            Assert.Equal(900.0, printer.LoadedFilaments.Single(f => f.FilamentId == tracked.Id).RemainingGrams);
            Assert.Null(printer.LoadedFilaments.Single(f => f.FilamentId == untracked.Id).RemainingGrams);
        }
    }

    private static AddFilamentDto Spool(string name, long? initialMg) => new()
    {
        DisplayName = name,
        Brand = "Generic",
        MaterialType = "PLA",
        MaterialCategoryNickname = "filament",
        DiameterMm = 1.75,
        InitialNominalWeightMg = initialMg,
        MaterialDensityGramPerCubicCm = 1.24,
        IsActive = true,
    };
}
