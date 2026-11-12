using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.IntegrationTests.Devices;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Seeds an owner whose spool carries a marker in every field a reader who is not the owner must
/// not receive, and makes requests as the owner (interactive, API key, device token), as another
/// signed-in user, or anonymously.
/// </summary>
public static class PublicReadSeed
{
    public const string FilamentNotesMarker = "redact-fil-notes-marker";
    public const string PurchaseNotesMarker = "redact-purchase-notes-marker";
    public const string UsageNotesMarker = "redact-usage-notes-marker";
    public const string StorageMarker = "redact-storage-marker";
    public const string PriceMarker = "876.54";
    public const string LoadedPrinterName = "Secret shop printer";

    /// <summary>The label of a usage row with no spool: the public page shows it as the material.</summary>
    public const string UntrackedRowLabel = "Mystery PETG from a friend";

    /// <summary>Every value that must stay with the owner, searched for in a reader's response body.</summary>
    public static readonly string[] PrivateMarkers =
    [
        FilamentNotesMarker, PurchaseNotesMarker, UsageNotesMarker, StorageMarker, PriceMarker, LoadedPrinterName,
    ];

    public sealed record Owner(User User, Printer Printer, Filament Filament);

    public enum Caller
    {
        Anonymous,
        OtherUser,
        Owner,
        OwnerApiKey,
        OwnerDevice,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Owner> OwnerAsync(
        CustomWebApplicationFactory factory, User.ProfileViewStatus view = User.ProfileViewStatus.Public)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            // A device-shaped subject, so the same user can also pair a device.
            OAuthUserId = DeviceTestKit.NewSubject("redact"),
            DisplayName = "Redaction owner",
            ViewStatus = view,
            Email = $"redact-{Guid.NewGuid():N}@example.test",
            EmailVerified = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        var printer = new Printer { Name = LoadedPrinterName, Make = "Prusa", Model = "MK4", UserId = user.Id, IsActive = true };
        db.Printers.Add(printer);
        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            SpoolNumber = 7,
            Brand = "Polymaker",
            DisplayName = "Galaxy Black PLA",
            ColorName = "Black",
            ColorHex = "111111",
            Colors = ["111111", "222222"],
            ColorPattern = PrintLogApi.Enums.ColorPatternType.Gradient,
            FinishType = PrintLogApi.Enums.FilamentFinishType.Matte,
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            InitialNominalWeightMg = 1_000_000,
            Source = Filament.SourceMeasurement.Weight,
            IsActive = true,
            IsFavorite = true,
            Notes = FilamentNotesMarker,
            PurchaseNotes = PurchaseNotesMarker,
            PurchasePriceValue = PriceMarker,
            PurchasePriceCurrency = "USD",
            StorageLocation = StorageMarker,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        db.Filaments.Add(filament);
        await db.SaveChangesAsync(Ct);

        db.PrinterFilament.Add(new PrinterFilament { PrinterId = printer.Id, FilamentId = filament.Id, LoadedDateTime = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        return new Owner(user, printer, filament);
    }

    /// <summary>A signed-in user who owns nothing the tests read.</summary>
    public static async Task<User> OtherUserAsync(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|redact-other-{Guid.NewGuid():N}",
            DisplayName = "Another user",
            ViewStatus = User.ProfileViewStatus.Public,
            Email = $"redact-other-{Guid.NewGuid():N}@example.test",
            EmailVerified = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    /// <summary>
    /// A print with one row on the owner's spool (carrying usage notes) and one row with no spool,
    /// whose notes are the material label.
    /// </summary>
    public static async Task<Print> PrintAsync(
        CustomWebApplicationFactory factory, Owner owner,
        PrintViewStatus view = PrintViewStatus.Public, Guid? projectId = null,
        int seconds = 5400, DateTimeOffset? start = null, string title = "Benchy")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = title,
            PrinterId = owner.Printer.Id,
            Status = PrintStatus.Success,
            ViewStatus = view,
            StartDate = start ?? new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero),
            PrintTimeInSeconds = seconds,
            ProjectId = projectId,
            CreatedById = owner.User.Id,
            UpdatedById = owner.User.Id,
            FilamentUsage =
            [
                new PrintFilament
                {
                    FilamentId = owner.Filament.Id,
                    AmountMg = 12_000,
                    Notes = UsageNotesMarker,
                    Source = PrintFilament.SourceMeasurement.Weight,
                    EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                },
                new PrintFilament
                {
                    FilamentId = null,
                    AmountMg = 3_000,
                    Notes = UntrackedRowLabel,
                    Source = PrintFilament.SourceMeasurement.Weight,
                    EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                },
            ],
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(Ct);
        return print;
    }

    public static async Task<Guid> ProjectAsync(
        CustomWebApplicationFactory factory, Owner owner, Project.ProjectViewStatus view, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var project = new Project
        {
            Name = name,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = view,
            CreatedById = owner.User.Id,
            UpdatedById = owner.User.Id,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(Ct);
        return project.Id;
    }

    /// <summary>
    /// Sends a GET as <paramref name="caller"/>. <paramref name="other"/> is required for
    /// <see cref="Caller.OtherUser"/>.
    /// </summary>
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string url, Caller caller, Owner owner, User? other = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        switch (caller)
        {
            case Caller.Anonymous:
                break;
            case Caller.OtherUser:
                request.Headers.Add(TestAuthHandler.TestUserIdHeader, (other ?? throw new ArgumentNullException(nameof(other))).OAuthUserId);
                break;
            case Caller.Owner:
                request.Headers.Add(TestAuthHandler.TestUserIdHeader, owner.User.OAuthUserId);
                break;
            case Caller.OwnerApiKey:
                request.Headers.Add("X-Api-Key", await ApiKeyAsync(client, owner));
                break;
            case Caller.OwnerDevice:
                var token = await DeviceTestKit.TokenAsync(client, owner.User.OAuthUserId!, [DeviceScopes.PrintsRead]);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
        }

        return await client.SendAsync(request, Ct);
    }

    public static async Task<JsonElement> GetJsonAsync(
        HttpClient client, string url, Caller caller, Owner owner, User? other = null)
    {
        var response = await GetAsync(client, url, caller, owner, other);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} as {caller}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string> ApiKeyAsync(HttpClient client, Owner owner)
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys")
        {
            Content = JsonContent.Create(new { Description = "Public read redaction test" }),
        };
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, owner.User.OAuthUserId);
        var created = await client.SendAsync(create, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct)).GetProperty("publicKey").GetString()!;
    }
}
