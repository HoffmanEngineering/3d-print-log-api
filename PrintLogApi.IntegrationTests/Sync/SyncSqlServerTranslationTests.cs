using Microsoft.EntityFrameworkCore;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models;
using PrintLogApi.Services.Sync;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// The snapshot's queries through the SQL Server provider, which the SQLite suite cannot vouch
/// for. The spool projection holds correlated sums (the 8124 shape; see
/// <c>FilamentSqlServerTranslationTests</c>), and the printer query holds two correlated top-1
/// subqueries. No database is involved: ToQueryString compiles the query through the provider.
/// </summary>
public class SyncSqlServerTranslationTests
{
    private static PrintLogContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

    [Fact]
    public void Spools_UsageAggregates_CarryNoOuterReference()
    {
        using var db = SqlServerContext();
        var sql = SyncSnapshotService.SpoolsQuery(db, 42).ToQueryString();

        // Every SUM, not only the first: the projection holds one per shared figure (remaining
        // and consumed each sum the usage rows and the adjustments), and an outer reference in
        // any of them is the 8124 shape.
        var aggregates = new List<string>();
        for (var start = sql.IndexOf("SUM(", StringComparison.Ordinal); start >= 0; start = sql.IndexOf("SUM(", start + 4, StringComparison.Ordinal))
        {
            aggregates.Add(Balanced(sql, start + 3));
        }

        Assert.True(aggregates.Count >= 4, $"Expected the usage and adjustment sums of both figures:\n{sql}");
        foreach (var aggregate in aggregates)
        {
            // [f] is the outer filament. Anything of its inside a SUM is the 8124 shape.
            Assert.DoesNotContain("[f].", aggregate);
        }

        // And the usage sums still convert, from the subquery's own join.
        var usage = aggregates.Where(a => a.Contains("CASE", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(usage);
        Assert.All(usage, a => Assert.Contains("MaterialDensityGramPerCubicCm", a));
        Assert.All(usage, a => Assert.Contains("DiameterMm", a));
    }

    /// <summary>The parenthesized text starting at <paramref name="open"/>, nested parentheses included.</summary>
    private static string Balanced(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Unbalanced aggregate at {open}:\n{text}");
    }

    /// <summary>
    /// EF Core 10 turns each correlated <c>FirstOrDefault</c> into a LEFT JOIN over a
    /// <c>ROW_NUMBER()</c> partitioned by printer, filtered to the user's rows: one statement,
    /// one pass per subquery, however many printers there are.
    /// </summary>
    [Fact]
    public void Printers_CurrentAndLastPrint_TranslateToPerPrinterTopOneJoins()
    {
        using var db = SqlServerContext();
        var sql = SyncSnapshotService.PrintersQuery(db, 42).ToQueryString();

        // Last maintenance, current print and last finished print: three joins. The print-time sum
        // and the two open-maintenance figures are scalar subqueries, not joins.
        Assert.Equal(3, CountOf(sql, "ROW_NUMBER() OVER(PARTITION BY"));
        Assert.Equal(3, CountOf(sql, "[row] <= 1"));
        Assert.Equal(3, CountOf(sql, "].[CreatedById] = @userId"));
        Assert.DoesNotContain("OUTER APPLY", sql);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>
/// The snapshot's cache-miss path executed on a real SQL Server. Skipped unless
/// PRINTLOG_SQLSERVER_TEST_CONNECTION is set; reuses the migrated scratch host.
/// </summary>
public class SyncSqlServerTests(EmailPipelineSqlServerTests.SqlServerFactory factory)
    : IClassFixture<EmailPipelineSqlServerTests.SqlServerFactory>
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Snapshot_ExecutesOnSqlServer()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");

        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        long userId, printerId;
        Guid spoolId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = new User { OAuthUserId = $"auth0|sync-sql-{Guid.NewGuid():N}", DisplayName = "Sync", ViewStatus = User.ProfileViewStatus.Private };
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);
            userId = user.Id;

            var printer = new Printer { Name = "Voron", UserId = userId, IsActive = true, CategoryNickname = "FDM", SlotCount = 2 };
            var spool = new Filament
            {
                Id = Guid.NewGuid(),
                DisplayName = "Black",
                MaterialType = "PLA",
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.24,
                DiameterMm = 1.75,
                InitialNominalWeightMg = 1_000_000,
                SpoolNumber = 1,
                Colors = ["#000000"],
                IsActive = true,
                CreatedById = userId,
                UpdatedById = userId,
                CreatedDate = now.UtcDateTime,
                UpdatedDate = now.UtcDateTime,
            };
            db.Printers.Add(printer);
            db.Filaments.Add(spool);
            await db.SaveChangesAsync(Ct);
            printerId = printer.Id;
            spoolId = spool.Id;

            db.PrinterFilament.Add(new PrinterFilament { PrinterId = printerId, FilamentId = spoolId, Slot = 0, SlotLabel = "T0", LoadedDateTime = now.AddDays(-1) });
            foreach (var (status, start) in new[] { (Print.PrintStatus.Success, now.AddDays(-1)), (Print.PrintStatus.Printing, now.AddMinutes(-10)) })
            {
                var print = new Print
                {
                    Title = status.ToString(),
                    PrinterId = printerId,
                    Status = status,
                    StartDate = start,
                    PrintTimeInSeconds = 600,
                    EstimatedPrintTimeInSeconds = 1200,
                    ViewStatus = Print.PrintViewStatus.Private,
                    CreatedById = userId,
                    UpdatedById = userId,
                };
                db.Prints.Add(print);
                await db.SaveChangesAsync(Ct);
                db.PrintFilament.Add(new PrintFilament { PrintId = print.Id, FilamentId = spoolId, LengthInM = 10, Source = PrintFilament.SourceMeasurement.Length });
            }

            db.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Kind = "moonraker",
                InstanceId = "sync-sql",
                DisplayName = "bridge",
                PrinterId = printerId,
                CreatedDate = now.UtcDateTime,
                LastSeenAt = now.AddMinutes(-2).UtcDateTime,
            });
            await db.SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var snapshot = await SyncSnapshotService.LoadCoreAsync(db, userId, Ct);
            var totals = await SyncSnapshotService.LoadTotalsAsync(db, userId, SyncMonth.Containing(now, SyncZone.Utc), Ct);
            var connections = await SyncSnapshotService.LoadConnectionsAsync(db, userId, now, Ct);

            var printer = Assert.Single(snapshot.Printers);
            Assert.Equal("Printing", printer.CurrentPrint?.Title);
            Assert.Equal("success", printer.LastFinishedPrint?.Status);
            Assert.Equal(spoolId, Assert.Single(printer.Loaded).SpoolId);
            var spool = Assert.Single(snapshot.Spools);
            // Two prints of 10 m of 1.75 mm PLA at 1.24 g/cm³: about 59.7 g.
            Assert.InRange(spool.UsedGrams, 59, 61);
            Assert.InRange(spool.RemainingGrams!.Value, 939, 941);
            Assert.Equal(2, totals.PrintCount);
            Assert.InRange(totals.Grams, 59, 61);
            Assert.Equal("online", connections[printerId].Status);
        }
    }
}
