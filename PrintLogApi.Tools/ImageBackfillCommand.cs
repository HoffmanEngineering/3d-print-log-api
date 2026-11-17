using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PrintLogApi;
using PrintLogApi.Services;
using PrintLogApi.Services.ImageBackfill;

/// <summary>
/// <c>image-backfill</c>: re-processes legacy print and project images to the canonical format
/// with thumbnails (<see cref="ImageBackfillRunner"/>), or with <c>--repair-transparency</c> repairs
/// rows an earlier run flattened to black (<see cref="ImageTransparencyRepairRunner"/>). A dry run
/// unless <c>--apply</c> is given.
/// Runbook: docs/image-backfill.md.
///
/// <para>Both connection strings come from the environment, under the names the API reads:
/// <c>ConnectionString__PrintLogDb</c> and <c>AZURE_STORAGE_CONNECTION_STRING</c>. They are
/// never taken as arguments (shell history) and never printed.</para>
/// </summary>
internal static class ImageBackfillCommand
{
    public const string Usage =
        "usage: image-backfill [--apply] [--batch-size <n>] [--limit <n>] [--print-id <id>] [--project-id <guid>]\n" +
        "       image-backfill --repair-transparency [--apply] [--limit <n>] [--from-log <file>]...";

    private const string DatabaseSetting = "ConnectionString:PrintLogDb";
    private const string StorageSetting = "AZURE_STORAGE_CONNECTION_STRING";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = new ImageBackfillOptions();
        var repair = false;
        var logFiles = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repair-transparency":
                    repair = true;
                    break;
                case "--from-log" when i + 1 < args.Length:
                    logFiles.Add(args[++i]);
                    break;
                case "--apply":
                    options = options with { Apply = true };
                    break;
                case "--dry-run":
                    options = options with { Apply = false };
                    break;
                case "--batch-size" when i + 1 < args.Length && int.TryParse(args[i + 1], out var batch) && batch > 0:
                    options = options with { BatchSize = batch };
                    i++;
                    break;
                case "--limit" when i + 1 < args.Length && int.TryParse(args[i + 1], out var limit) && limit > 0:
                    options = options with { Limit = limit };
                    i++;
                    break;
                case "--print-id" when i + 1 < args.Length && long.TryParse(args[i + 1], out var printId):
                    options = options with { PrintId = printId };
                    i++;
                    break;
                case "--project-id" when i + 1 < args.Length && Guid.TryParse(args[i + 1], out var projectId):
                    options = options with { ProjectId = projectId };
                    i++;
                    break;
                default:
                    return Fail($"unknown, incomplete or invalid argument: {args[i]}");
            }
        }

        if (repair && (options.PrintId is not null || options.ProjectId is not null
            || options.BatchSize != ImageBackfillOptions.DefaultBatchSize))
            return Fail("--repair-transparency takes only --apply, --limit and --from-log");
        if (!repair && logFiles.Count > 0)
            return Fail("--from-log is only for --repair-transparency");

        // Read the logs before connecting to anything, so a typo fails fast.
        var fromLog = new List<ArchivedOriginal>();
        foreach (var file in logFiles)
        {
            if (!File.Exists(file))
                return Fail($"log file not found: {file}");
            var parsed = BackfillLog.Parse(await File.ReadAllLinesAsync(file));
            Console.WriteLine($"Log {file}: {parsed.Entries.Count} archived rows");
            foreach (var line in parsed.Rejected)
                Console.WriteLine($"  ignored, archive container does not match the kind: {line}");
            fromLog.AddRange(parsed.Entries);
        }

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var database = configuration[DatabaseSetting];
        if (string.IsNullOrWhiteSpace(database))
            return Fail("set the database connection string in ConnectionString__PrintLogDb");
        if (string.IsNullOrWhiteSpace(configuration[StorageSetting]))
            return Fail($"set the storage connection string in {StorageSetting}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Stops between steps; a row caught mid-write is cleaned up as a failed one is.
            e.Cancel = true;
            cts.Cancel();
        };

        // Names only, so a run against the wrong environment is obvious before it does anything.
        Console.WriteLine($"Database: {Describe(database, "Initial Catalog", "Database")}");
        Console.WriteLine($"Storage account: {Describe(configuration[StorageSetting]!, "AccountName")}");
        Console.WriteLine(options.Apply ? "Mode: APPLY" : "Mode: dry run");
        if (repair)
            Console.WriteLine("Command: repair transparency");

        var dbOptions = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer(database, sql => sql.EnableRetryOnFailure())
            .Options;
        await using var db = new PrintLogContext(dbOptions);
        var blobs = new AzureBlobStorageService(configuration, TimeProvider.System, new BlobContainerProvisioner());

        if (repair)
        {
            var repairRunner = new ImageTransparencyRepairRunner(
                db, blobs, new ImageProcessingService(), new ProcessedImageWriter(db, blobs), Console.Out);
            TransparencyRepairReport repairReport;
            try
            {
                repairReport = await repairRunner.RunAsync(
                    new TransparencyRepairOptions { Apply = options.Apply, Limit = options.Limit, FromLog = fromLog },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("canceled; rows already repaired stay repaired, and a re-run continues from the rest");
                return 130;
            }

            Console.WriteLine();
            repairReport.WriteSummary(Console.Out);
            return repairReport.Failed.Count > 0 ? 1 : 0;
        }

        var runner = new ImageBackfillRunner(
            db, blobs, new ImageProcessingService(), new ProcessedImageWriter(db, blobs), Console.Out);

        ImageBackfillReport report;
        try
        {
            report = await runner.RunAsync(options, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("canceled; rows already written stay written, and a re-run continues from the rest");
            return 130;
        }

        Console.WriteLine();
        report.WriteSummary(Console.Out);
        return report.Failed.Count > 0 ? 1 : 0;
    }

    /// <summary>One value out of a connection string, or "(unknown)". Never the whole string.</summary>
    private static string Describe(string connectionString, params string[] keys)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in keys)
            {
                if (builder.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text)
                    return text;
            }
        }
        catch (ArgumentException)
        {
            // Malformed: the connection itself will report it, without echoing the string.
        }
        return "(unknown)";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
