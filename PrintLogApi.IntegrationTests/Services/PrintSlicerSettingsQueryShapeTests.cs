using System.Data.Common;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using PrintLogApi.Services.SlicerSettings;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// A resend of a print's slicer settings replaces the record's <c>Settings</c> without reading
/// it (#309). The column is up to 512 KiB of nvarchar per row, on a table of about 51 GB, and the
/// old code loaded it only to overwrite it; that read is where the endpoint's SQL timeouts were.
/// The behavior of a resend is pinned in <c>PrintsControllerSlicerSettingsTests</c>; this pins
/// the reads, which no result can show.
/// </summary>
public class PrintSlicerSettingsQueryShapeTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>The service over the factory's own SQLite connection, with a recording context.</summary>
    private (PrintSlicerSettingsService Service, CommandRecorder Recorder) RecordingService()
    {
        var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var connection = sp.GetRequiredService<PrintLogContext>().Database.GetDbConnection();

        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        return (new PrintSlicerSettingsService(
            new PrintLogContext(options),
            sp.GetRequiredService<ICacheVersionService>(),
            sp.GetRequiredService<TelemetryClient>()), recorder);
    }

    private async Task<long> CreatePrintAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = $"slicer shape {Guid.NewGuid():N}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = Print.PrintStatus.Printing,
            ViewStatus = Print.PrintViewStatus.Private,
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId,
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print.Id;
    }

    private static PrintSlicerSettingsDto Metadata(string version, double layerHeight) => new()
    {
        Metadata = new SlicerMetadataDto
        {
            Slicer = "OrcaSlicer",
            SlicerVersion = version,
            LayerHeightMm = layerHeight,
        },
    };

    private static string Normalize(string sql) =>
        sql.Replace("[", "", StringComparison.Ordinal)
           .Replace("]", "", StringComparison.Ordinal)
           .Replace("\"", "", StringComparison.Ordinal);

    [Fact]
    public async Task Resend_NeverReadsTheStoredSettings()
    {
        var printId = await CreatePrintAsync();
        var (service, recorder) = RecordingService();
        await service.Record(printId, Metadata("2.3.0", 0.2), IntegrationTestSeeder.TestUserId, Ct);

        var (resendService, resendRecorder) = RecordingService();
        var result = await resendService.Record(printId, Metadata("2.3.1", 0.28), IntegrationTestSeeder.TestUserId, Ct);

        Assert.True(result.Replaced);
        var settingsReads = resendRecorder.Commands
            .Select(Normalize)
            .Where(sql => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("FROM CuraSettings", StringComparison.Ordinal))
            .ToList();
        // A sanity check on the matcher: the record is still looked up, just not read whole.
        Assert.NotEmpty(settingsReads);
        Assert.All(settingsReads, sql =>
        {
            var selectList = sql[..sql.IndexOf("FROM CuraSettings", StringComparison.Ordinal)];
            Assert.DoesNotContain(".Settings", selectList, StringComparison.Ordinal);
        });
        Assert.NotEmpty(recorder.Commands);
    }

    [Fact]
    public async Task Resend_ReplacesTheRecordInPlace_AndKeepsWhenAndWhoseItWas()
    {
        var printId = await CreatePrintAsync();
        var (service, _) = RecordingService();
        await service.Record(printId, Metadata("2.3.0", 0.2), IntegrationTestSeeder.TestUserId, Ct);

        CuraSetting first;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            first = await db.CuraSettings.AsNoTracking().SingleAsync(c => c.PrintId == printId, Ct);
            // A record a plugin version once wrote to: a connector's resend clears it, as before.
            await db.CuraSettings.Where(c => c.Id == first.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.PluginVersion, "1.2.3"), Ct);
        }

        var (resendService, _) = RecordingService();
        await resendService.Record(printId, Metadata("2.3.1", 0.28), IntegrationTestSeeder.TestUserId, Ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var record = await db.CuraSettings.AsNoTracking().SingleAsync(c => c.PrintId == printId, Ct);
            Assert.Equal(first.Id, record.Id);
            Assert.Equal(first.CreatedDate, record.CreatedDate);
            Assert.Equal(IntegrationTestSeeder.TestUserId, record.UserId);
            Assert.Null(record.PluginVersion);
            Assert.Equal("OrcaSlicer", record.Slicer);
            Assert.Equal("2.3.1", record.CuraVersion);
            Assert.Equal("0.28", record.Settings.GetProperty("settings").GetProperty("layer_height").GetString());

            var print = await db.Prints.AsNoTracking().SingleAsync(p => p.Id == printId, Ct);
            Assert.Equal("orcaslicer", print.Slicer);
            Assert.Equal("2.3.1", print.SlicerVersion);
        }
    }
}
