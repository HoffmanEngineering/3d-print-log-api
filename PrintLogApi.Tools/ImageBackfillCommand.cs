using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PrintLogApi;
using PrintLogApi.Services;
using PrintLogApi.Services.ImageBackfill;

/// <summary>
/// <c>image-backfill</c>: re-processes legacy print and project images to the canonical format
/// with thumbnails (<see cref="ImageBackfillRunner"/>). A dry run unless <c>--apply</c> is given.
/// Runbook: docs/image-backfill.md.
///
/// <para>Both connection strings come from the environment, under the names the API reads:
/// <c>ConnectionString__PrintLogDb</c> and <c>AZURE_STORAGE_CONNECTION_STRING</c>. They are
/// never taken as arguments (shell history) and never printed.</para>
/// </summary>
internal static class ImageBackfillCommand
{
    public const string Usage =
        "usage: image-backfill [--apply] [--batch-size <n>] [--limit <n>] [--print-id <id>] [--project-id <guid>]";

    private const string DatabaseSetting = "ConnectionString:PrintLogDb";
    private const string StorageSetting = "AZURE_STORAGE_CONNECTION_STRING";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = new ImageBackfillOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
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

        var dbOptions = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer(database, sql => sql.EnableRetryOnFailure())
            .Options;
        await using var db = new PrintLogContext(dbOptions);
        var blobs = new AzureBlobStorageService(configuration, TimeProvider.System, new BlobContainerProvisioner());
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
