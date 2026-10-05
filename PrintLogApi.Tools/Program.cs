using Microsoft.EntityFrameworkCore;
using PrintLogApi;
using PrintLogApi.Email.Backfill;

// Usage: PrintLogApi.Tools email-backfill --file <export.json[.gz]> --connection <connection string> [--dry-run]
const string Usage = "usage: email-backfill --file <path> --connection <connection string> [--dry-run]";

if (args.Length == 0 || args[0] != "email-backfill")
{
    Console.Error.WriteLine(Usage);
    return 2;
}

string? file = null;
string? connection = null;
var dryRun = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length:
            file = args[++i];
            break;
        case "--connection" when i + 1 < args.Length:
            connection = args[++i];
            break;
        case "--dry-run":
            dryRun = true;
            break;
        default:
            Console.Error.WriteLine($"unknown or incomplete argument: {args[i]}");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

if (!File.Exists(file))
{
    Console.Error.WriteLine($"file not found: {file}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var options = new DbContextOptionsBuilder<PrintLogContext>()
    .UseSqlServer(connection, sql => sql.EnableRetryOnFailure())
    .Options;

await using var db = new PrintLogContext(options);
await using var stream = File.OpenRead(file);

var result = await new EmailBackfillImporter(db, TimeProvider.System)
    .ImportAsync(Auth0ExportReader.ReadAsync(stream, cts.Token), dryRun, cts.Token);

Console.WriteLine(dryRun ? "DRY RUN - nothing was written" : "Backfill complete");
Console.WriteLine($"  Read        {result.Read,8}");
Console.WriteLine($"  Matched     {result.Matched,8}");
Console.WriteLine($"  Updated     {result.Updated,8}");
Console.WriteLine($"  Unmatched   {result.Unmatched,8}");
Console.WriteLine($"  Verified    {result.Verified,8}");
Console.WriteLine($"  Unverified  {result.Unverified,8}");
return 0;
