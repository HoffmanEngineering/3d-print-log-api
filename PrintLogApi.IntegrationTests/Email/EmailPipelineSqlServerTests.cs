using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.IntegrationTests.Email.Campaigns;
using PrintLogApi.Models;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>
/// The whole pipeline (every campaign's due query and render, the evaluator's insert, the
/// dispatcher's claim and gates) against a real SQL Server, on a scratch database built from
/// the migrations. SQLite accepts queries SQL Server rejects (error 8124 among them), so the
/// rest of the suite cannot vouch for these.
///
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION points at a server; the database named in
/// it is ignored, and a uniquely named one is created and dropped.
/// </summary>
public class EmailPipelineSqlServerTests : IClassFixture<EmailPipelineSqlServerTests.SqlServerFactory>
{
    private const string Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private readonly SqlServerFactory _factory;

    public EmailPipelineSqlServerTests(SqlServerFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllCampaigns_QueueRenderAndDispatch_OnSqlServer()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");

        // 09:00 on Dec 1 in America/Chicago.
        var now = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PrintLogContext>();

        // Recap: a print in November.
        var recap = await EmailTestData.CreateUserAsync(db, createdDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var recapPrinter = await CampaignTestData.PrinterAsync(db, recap.Id);
        await CampaignTestData.PrintAsync(db, recap.Id, recapPrinter.Id, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

        // Printer silent: three Moonraker prints, the last 15 days ago.
        var silent = await EmailTestData.CreateUserAsync(db, createdDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var silentPrinter = await CampaignTestData.PrinterAsync(db, silent.Id, "Voron");
        foreach (var daysAgo in new[] { 40, 25, 15 })
        {
            await CampaignTestData.PrintAsync(db, silent.Id, silentPrinter.Id, now.AddDays(-daysAgo), PrintSource.Moonraker);
        }

        // Onboarding: signed up an hour ago.
        var newcomer = await EmailTestData.CreateUserAsync(db, createdDate: now.AddHours(-1));

        var campaigns = sp.GetServices<IEmailCampaign>().ToList();
        var settings = new EmailOptions
        {
            Enabled = true,
            HoldoutPercent = 0,
            MaxSendsPerSecond = 1000,
            Notice = new NoticeOptions { Required = false },
            UnsubscribeSigningKeys = [Key],
            SuppressionPeppers = [Key],
            WebBaseUrl = "https://www.3dprintlog.test",
            ApiBaseUrl = "https://api.3dprintlog.test",
            PostalAddress = "PO Box 0, Testville, USA",
        };
        foreach (var campaign in campaigns)
        {
            settings.Campaigns[EmailOptions.CampaignKey(campaign.Name)] = new CampaignOptions { Enabled = true };
        }

        var options = MsOptions.Create(settings);
        var telemetry = new TelemetryClient(new TelemetryConfiguration());

        // Twice: the second run exercises the dedupe query against rows that exist.
        var evaluator = new CampaignEvaluator(db, campaigns, options, telemetry);
        await evaluator.RunOnceAsync(now, Ct);
        await evaluator.RunOnceAsync(now, Ct);

        var queued = await db.EmailOutbox.AsNoTracking().ToListAsync(Ct);
        Assert.Contains(queued, o => o.UserId == recap.Id && o.Campaign == "monthly-recap" && o.PeriodKey == "2026-11");
        Assert.Contains(queued, o => o.UserId == silent.Id && o.Campaign == "printer-silent");
        Assert.Contains(queued, o => o.UserId == newcomer.Id && o.Campaign == "onboarding");

        // Every queued row renders, whichever one the dispatcher would pick first.
        foreach (var row in queued)
        {
            var rendered = await campaigns.Single(c => c.Name == row.Campaign).RenderAsync(row, Ct);
            Assert.NotNull(rendered);
        }

        var clock = new SettableTimeProvider(now);
        var transport = new FakeEmailTransport();
        var result = await new EmailDispatcher(
                db,
                campaigns,
                new EmailPreferenceService(db, telemetry),
                new FakeVerificationRefresher(),
                new EmailAddressHasher(options),
                transport,
                new EmailTokenService(options, clock),
                new EmailLinkBuilder(options),
                options,
                clock,
                telemetry)
            .RunOnceAsync(now, Ct);

        Assert.Equal(0, result.Failed);
        Assert.Contains(transport.Sent, m => m.ToAddress == recap.Email);
        Assert.Contains(transport.Sent, m => m.ToAddress == silent.Email);
        Assert.Contains(transport.Sent, m => m.ToAddress == newcomer.Email);
    }

    /// <summary>
    /// The narrowed monthly recap and printer-silent due queries (the recap's single padded-month
    /// pass with its outbox anti-join, printer silent's GROUP BY / HAVING MAX candidate query and
    /// its IN-list history load) translate and run on SQL Server, and agree with the queries they
    /// replaced at ticks inside, at the ends of and outside the recap's send window.
    /// </summary>
    [Fact]
    public async Task NarrowedCampaignQueries_MatchTheOldOnes_OnSqlServer()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");

        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<PrintLogContext>();
        var settings = CampaignQueryTestKit.Options();
        var ids = new HashSet<long>();
        var november = new DateOnly(2026, 11, 1);

        async Task<(long UserId, long PrinterId)> UserAsync(string? zone)
        {
            var user = await EmailTestData.CreateUserAsync(db, createdDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            ids.Add(user.Id);
            if (zone is not null)
            {
                await CampaignTestData.SetZoneAsync(db, user.Id, zone);
            }

            return (user.Id, (await CampaignTestData.PrinterAsync(db, user.Id)).Id);
        }

        // Recap: prints on and beside the local month edges of the extreme zones, one well inside.
        foreach (var zoneId in new string?[] { null, "Pacific/Kiritimati", "Etc/GMT+12", "Pacific/Auckland" })
        {
            var zone = EmailTimeZones.For(zoneId, settings.DefaultTimeZone);
            var localStart = EmailTimeZones.AtLocal(november, 0, zone);
            var localEnd = EmailTimeZones.AtLocal(november.AddMonths(1), 0, zone);
            foreach (var at in new[] { localStart, localStart.AddTicks(-10), localEnd, localEnd.AddTicks(-10), new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero) })
            {
                var (userId, printerId) = await UserAsync(zoneId);
                await CampaignTestData.PrintAsync(db, userId, printerId, at);
            }
        }

        // Already queued for November: the narrowed query leaves it out, the old one did not.
        var (queued, queuedPrinter) = await UserAsync(null);
        await CampaignTestData.PrintAsync(db, queued, queuedPrinter, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        db.EmailOutbox.Add(EmailTestData.OutboxRow(queued, MonthlyRecapCampaign.CampaignName, "2026-11", new DateTimeOffset(2026, 11, 30, 19, 0, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync(Ct);

        // Printer silent: quiet 15 days with history, quiet 15 days without enough, still reporting.
        var silentNow = new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero);
        foreach (var daysAgo in new[] { new[] { 15, 25, 35 }, [15, 25], [2, 12, 22] })
        {
            var (userId, printerId) = await UserAsync("Europe/Berlin");
            foreach (var d in daysAgo)
            {
                await CampaignTestData.PrintAsync(db, userId, printerId, silentNow.AddDays(-d), PrintSource.Moonraker);
            }
        }

        var recap = CampaignQueryTestKit.MonthlyRecap(sp, db, settings);
        var silent = CampaignQueryTestKit.PrinterSilent(sp, db, settings);
        var recapDue = 0;
        var silentDue = 0;
        foreach (var now in new[]
                 {
                     new DateTimeOffset(2026, 11, 30, 18, 30, 0, TimeSpan.Zero),
                     new DateTimeOffset(2026, 11, 30, 18, 45, 0, TimeSpan.Zero),
                     silentNow,
                     new DateTimeOffset(2026, 12, 6, 20, 45, 0, TimeSpan.Zero),
                     new DateTimeOffset(2026, 12, 6, 21, 0, 0, TimeSpan.Zero),
                     new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero),
                 })
        {
            var expectedRecap = (await LegacyCampaignQueries.MonthlyRecapDueAsync(db, settings, now, Ct))
                .Where(d => ids.Contains(d.UserId) && d.UserId != queued).OrderBy(d => d.UserId).ToList();
            var actualRecap = (await recap.FindDueAsync(now, Ct)).Where(d => ids.Contains(d.UserId)).OrderBy(d => d.UserId).ToList();
            Assert.Equal(expectedRecap, actualRecap);
            recapDue += actualRecap.Count;

            var expectedSilent = (await LegacyCampaignQueries.PrinterSilentDueAsync(db, settings, now, Ct))
                .Where(d => ids.Contains(d.UserId)).OrderBy(d => d.UserId).ToList();
            var actualSilent = (await silent.FindDueAsync(now, Ct)).Where(d => ids.Contains(d.UserId)).OrderBy(d => d.UserId).ToList();
            Assert.Equal(expectedSilent, actualSilent);
            silentDue += actualSilent.Count;
        }

        Assert.True(recapDue > 0 && silentDue > 0, $"Nothing was due (recap {recapDue}, silent {silentDue}).");
    }

    /// <summary>The standard test host on a scratch SQL Server database, migrated rather than created.</summary>
    public sealed class SqlServerFactory : CustomWebApplicationFactory
    {
        private readonly string? _connection = Connection is null
            ? null
            : new SqlConnectionStringBuilder(Connection) { InitialCatalog = $"EmailPipeline_{Guid.NewGuid():N}" }.ConnectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (_connection is null)
            {
                return;
            }

            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Onboarding:StartDate"] = "2026-01-01T00:00:00Z",
            }));

            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(d =>
                             d.ServiceType == typeof(DbContextOptions<PrintLogContext>) ||
                             d.ServiceType == typeof(DbContextOptions) ||
                             d.ServiceType == typeof(PrintLogContext) ||
                             d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<PrintLogContext>((sp, options) =>
                {
                    options.UseSqlServer(_connection);
                    options.AddAchievementInterceptors(sp);
                    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                });
            });
        }

        protected override void PrepareDatabase(PrintLogContext db)
        {
            if (_connection is null)
            {
                base.PrepareDatabase(db);
                return;
            }

            db.Database.Migrate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _connection is not null)
            {
                var name = new SqlConnectionStringBuilder(_connection).InitialCatalog;
                var master = new SqlConnectionStringBuilder(_connection) { InitialCatalog = "master" }.ConnectionString;
                SqlConnection.ClearAllPools();
                using var conn = new SqlConnection(master);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
                cmd.ExecuteNonQuery();
            }

            base.Dispose(disposing);
        }
    }
}
