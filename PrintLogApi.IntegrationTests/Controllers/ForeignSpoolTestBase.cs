using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Seeding and requests shared by the ForeignSpool*Tests classes. A printer's loaded spools and a
/// print's usage rows may only name spools their owner owns: the create path must refuse anything
/// else, and rows that already reference another user's spool (written before that check existed)
/// must be ignored by every read and never be written to. Those rows are seeded straight into the
/// database, as production may already hold them.
/// </summary>
public abstract class ForeignSpoolTestBase(CustomWebApplicationFactory factory)
{
    protected const int InitialMg = 1_000_000;
    protected const int OwnPrintMg = 12_000;
    protected const string NotesMarker = "owner-private-notes";

    protected readonly CustomWebApplicationFactory _factory = factory;
    protected readonly HttpClient _client = factory.CreateClient();
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected sealed record Owner(User User, Printer Printer, Filament Filament);

    // ---------------------------------------------------------------- seeding

    protected async Task<Owner> SeedOwnerAsync(bool loadOwnSpool = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|foreign-spool-{Guid.NewGuid():N}",
            DisplayName = "Spool owner",
            ViewStatus = User.ProfileViewStatus.Public,
            Email = $"fs-{Guid.NewGuid():N}@example.test",
            EmailVerified = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        var printer = new Printer { Name = $"Printer {Guid.NewGuid():N}", Make = "Prusa", Model = "MK4", UserId = user.Id, IsActive = true };
        db.Printers.Add(printer);
        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            Brand = "Polymaker",
            DisplayName = "Galaxy Black PLA",
            ColorName = "Black",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            InitialNominalWeightMg = InitialMg,
            Source = Filament.SourceMeasurement.Weight,
            IsActive = true,
            Notes = $"{NotesMarker}-{user.Id}",
            PurchasePriceValue = "876.54",
            StorageLocation = "owner-private-shelf",
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        db.Filaments.Add(filament);
        await db.SaveChangesAsync(Ct);
        if (loadOwnSpool)
        {
            db.PrinterFilament.Add(new PrinterFilament { PrinterId = printer.Id, FilamentId = filament.Id, LoadedDateTime = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }
        return new Owner(user, printer, filament);
    }

    /// <summary>A print by <paramref name="creator"/> on <paramref name="printer"/>, with one usage row on <paramref name="filamentId"/>.</summary>
    protected async Task<Print> SeedPrintAsync(User creator, Printer printer, Guid? filamentId, int amountMg,
        PrintStatus status = PrintStatus.Success, int? slot = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = "Benchy",
            PrinterId = printer.Id,
            Status = status,
            ViewStatus = PrintViewStatus.Public,
            StartDate = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero),
            PrintTimeInSeconds = status == PrintStatus.Printing ? null : 5400,
            CreatedById = creator.Id,
            UpdatedById = creator.Id,
            FilamentUsage =
            [
                new PrintFilament
                {
                    FilamentId = filamentId,
                    Slot = slot,
                    AmountMg = status == PrintStatus.Printing ? null : amountMg,
                    EstimatedAmountMg = amountMg,
                    Source = PrintFilament.SourceMeasurement.Weight,
                    EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                },
            ],
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print;
    }

    /// <summary>A loaded row that names another user's spool: what the create path used to allow.</summary>
    protected async Task SeedLoadAsync(Printer printer, Guid filamentId, int? slot = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.PrinterFilament.Add(new PrinterFilament { PrinterId = printer.Id, FilamentId = filamentId, Slot = slot, LoadedDateTime = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The victim with one print of their own on their spool, and someone else with a printer and spool.</summary>
    protected async Task<(Owner Victim, Owner Other)> SeedPairAsync(bool otherLoadsOwnSpool = true)
    {
        var victim = await SeedOwnerAsync();
        var other = await SeedOwnerAsync(otherLoadsOwnSpool);
        await SeedPrintAsync(victim.User, victim.Printer, victim.Filament.Id, OwnPrintMg);
        return (victim, other);
    }

    // ---------------------------------------------------------------- helpers

    protected static HttpRequestMessage As(HttpRequestMessage request, User user)
    {
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return request;
    }

    protected static HttpRequestMessage PrinterBody(HttpMethod method, string url, Guid filamentId, long? id = null) => new(method, url)
    {
        Content = JsonContent.Create(new AddPrinterDTO
        {
            Id = id,
            Name = "Printer with spools",
            Make = "x",
            Model = "y",
            IsActive = true,
            LoadedFilaments = [new AddPrinterFilamentDto { Id = Guid.Empty, FilamentId = filamentId }],
        }),
    };

    protected async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request)
    {
        var response = await _client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    protected async Task<JsonElement> GetJsonAsync(string url, User user)
    {
        var (status, body) = await SendAsync(As(new HttpRequestMessage(HttpMethod.Get, url), user));
        Assert.True(status == HttpStatusCode.OK, $"GET {url}: {(int)status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    protected async Task<long?> RemainingAsync(Owner owner)
    {
        var detail = await GetJsonAsync($"/api/Filaments/{owner.Filament.Id}", owner.User);
        var remaining = detail.GetProperty("filamentRemaining");
        return remaining.ValueKind == JsonValueKind.Null ? null : remaining.GetInt64();
    }

    protected async Task<List<PrinterFilament>> LoadedRowsForFilamentAsync(Guid filamentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.PrinterFilament.IgnoreQueryFilters().AsNoTracking().Where(pf => pf.FilamentId == filamentId).ToListAsync(Ct);
    }

    protected async Task<int> PrinterCountAsync(long userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Printers.CountAsync(p => p.UserId == userId, Ct);
    }

    protected async Task<Print> PrintAsync(long id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints.Include(p => p.FilamentUsage).AsNoTracking().SingleAsync(p => p.Id == id, Ct);
    }

    protected async Task<Print> PrintForRunAsync(string run)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints.Include(p => p.FilamentUsage).AsNoTracking().SingleAsync(p => p.FileName!.Contains(run), Ct);
    }

    protected static string Fixture(string name) =>
        System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "print-events", name));

    protected async Task NotifierAsync(User user, string fixture, long printerId, string run, double? filamentUsedMm = null)
    {
        var body = Fixture(fixture).Replace("__PRINTER_ID__", printerId.ToString()).Replace("__RUN__", run);
        if (filamentUsedMm is { } mm)
        {
            body = body.Replace("\\\"filament_used\\\": 4210.33241", $"\\\"filament_used\\\": {mm:0.0}");
        }
        var (status, text) = await SendAsync(As(new HttpRequestMessage(HttpMethod.Post, "/api/Moonraker/notifier")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, user));
        Assert.True(status == HttpStatusCode.OK, $"notifier {fixture}: {(int)status} {text}");
    }

    protected Task<(HttpStatusCode Status, string Body)> CompleteAsync(User user, long printId, CompletePrintFilamentUsageDto usage) =>
        SendAsync(As(new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/complete")
        {
            Content = JsonContent.Create(new CompletePrintDto
            {
                Status = PrintStatus.Success,
                PrintTimeInSeconds = 100,
                FilamentUsage = [usage],
            }),
        }, user));
}
