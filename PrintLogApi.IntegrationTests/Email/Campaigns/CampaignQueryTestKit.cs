using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.Services.Analytics;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

/// <summary>
/// Builds the two campaigns whose due queries were narrowed, over any context, plus a recorder
/// for the SQL those queries send.
/// </summary>
internal static class CampaignQueryTestKit
{
    public static EmailOptions Options() => new()
    {
        WebBaseUrl = "https://www.3dprintlog.test",
        ApiBaseUrl = "https://api.3dprintlog.test",
        PostalAddress = "PO Box 0, Testville, USA",
    };

    public static MonthlyRecapCampaign MonthlyRecap(IServiceProvider sp, PrintLogContext db, EmailOptions? settings = null)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(settings ?? Options());
        var links = new EmailLinkBuilder(wrapped);
        return new MonthlyRecapCampaign(
            db,
            sp.GetRequiredService<IAnalyticsService>(),
            sp.GetRequiredService<IEmailTemplateRenderer>(),
            new EmailFooterFactory(links, sp.GetRequiredService<IEmailTokenService>(), wrapped),
            links,
            new EmailAssets(wrapped),
            wrapped);
    }

    public static PrinterSilentCampaign PrinterSilent(IServiceProvider sp, PrintLogContext db, EmailOptions? settings = null)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(settings ?? Options());
        var links = new EmailLinkBuilder(wrapped);
        return new PrinterSilentCampaign(
            db,
            sp.GetRequiredService<IEmailTemplateRenderer>(),
            new EmailFooterFactory(links, sp.GetRequiredService<IEmailTokenService>(), wrapped),
            links,
            wrapped);
    }

    /// <summary>A second context over the scope's own SQLite connection that records every query.</summary>
    public static (PrintLogContext Db, CommandRecorder Recorder) RecordingContext(IServiceProvider sp)
    {
        var connection = sp.GetRequiredService<PrintLogContext>().Database.GetDbConnection();
        var recorder = new CommandRecorder();
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(recorder)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return (new PrintLogContext(options), recorder);
    }

    internal sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        /// <summary>Commands that read the Prints table (SQLite quotes it as "Prints").</summary>
        public List<string> PrintsQueries => Commands.Where(c => c.Contains("\"Prints\"", StringComparison.Ordinal)).ToList();

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
}
