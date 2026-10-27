using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Seo;
using SkiaSharp;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// <c>GET /api/{Prints,Projects,Users}/{id}/seo</c>: the metadata the Cloudflare Worker writes
/// into the app shell for crawlers and link previews. 404 unless the entity is public, and
/// nothing the public page hides.
/// </summary>
public class SeoEndpointsTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary><c>Email:WebBaseUrl</c> in appsettings.IntegrationTesting.json.</summary>
    private const string Web = "https://www.3dprintlog.test";

    // Strings that live on rows the public page never shows. None may reach /seo.
    private const string FilamentNotesMarker = "filament-notes-marker";
    private const string PurchaseNotesMarker = "purchase-notes-marker";
    private const string UsageNotesMarker = "usage-notes-marker";
    private const string PurchasePriceMarker = "987.65";
    private const string ExternalIdMarker = "external-id-marker";
    private const string EmailMarker = "seo-owner@example.test";

    private sealed record Seeded(User User, Printer Printer, Filament Filament);

    private async Task<Seeded> SeedUserAsync(
        User.ProfileViewStatus viewStatus = User.ProfileViewStatus.Public,
        string? displayName = "Ada Maker", string? bio = null, string? profilePicture = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|seo-{Guid.NewGuid()}",
            DisplayName = displayName,
            Bio = bio,
            ProfilePicture = profilePicture,
            ViewStatus = viewStatus,
            Email = EmailMarker,
            EmailVerified = true,
            CreatedDate = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);

        var printer = new Printer { Name = "Workshop printer", Make = "Prusa", Model = "MK4", UserId = user.Id, IsActive = true };
        context.Printers.Add(printer);
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
            InitialNominalWeightMg = 1_000_000,
            Source = Filament.SourceMeasurement.Weight,
            IsActive = true,
            Notes = FilamentNotesMarker,
            PurchaseNotes = PurchaseNotesMarker,
            PurchasePriceValue = PurchasePriceMarker,
            PurchasePriceCurrency = "USD",
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        context.Filaments.Add(filament);
        await context.SaveChangesAsync(Ct);
        return new Seeded(user, printer, filament);
    }

    private async Task<long> SeedPrintAsync(
        Seeded owner, PrintViewStatus viewStatus = PrintViewStatus.Public, string? title = "Benchy",
        string? notes = null, Guid? projectId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = title,
            Notes = notes,
            PrinterId = owner.Printer.Id,
            Status = PrintStatus.Success,
            ViewStatus = viewStatus,
            StartDate = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero),
            PrintTimeInSeconds = 5400,
            EstimatedPrintTimeInSeconds = 5000,
            ProjectId = projectId,
            ExternalSource = "moonraker",
            ExternalId = $"{ExternalIdMarker}-{Guid.NewGuid():N}",
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
            ],
        };
        context.Prints.Add(print);
        await context.SaveChangesAsync(Ct);
        return print.Id;
    }

    private async Task<Guid> SeedProjectAsync(
        Seeded owner, Project.ProjectViewStatus viewStatus = Project.ProjectViewStatus.Public,
        string? description = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var project = new Project
        {
            Name = "Desk organizer",
            Description = description,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = viewStatus,
            CreatedById = owner.User.Id,
            UpdatedById = owner.User.Id,
        };
        context.Projects.Add(project);
        await context.SaveChangesAsync(Ct);
        return project.Id;
    }

    private static ByteArrayContent Png(int width = 1600, int height = 1000)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.SeaGreen);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private async Task<int> UploadPrintImageAsync(Seeded owner, long printId, bool isDefault = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/image");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, owner.User.OAuthUserId);
        var form = new MultipartFormDataContent { { Png(), "image", "photo.png" } };
        if (isDefault) form.Add(new StringContent("true"), "isDefault");
        request.Content = form;
        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
    }

    private async Task UploadProjectImageAsync(Seeded owner, Guid projectId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Projects/{projectId}/images");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, owner.User.OAuthUserId);
        request.Content = new MultipartFormDataContent { { Png(1000, 1500), "file", "project.png" } };
        var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<(HttpResponseMessage Response, string Body, SeoMetadataDto? Dto)> GetSeoAsync(
        string url, string? asOAuthUser = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (asOAuthUser is not null) request.Headers.Add(TestAuthHandler.TestUserIdHeader, asOAuthUser);
        var response = await _client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var dto = response.IsSuccessStatusCode
            ? JsonSerializer.Deserialize<SeoMetadataDto>(body, JsonSerializerOptions.Web)
            : null;
        return (response, body, dto);
    }

    private static void AssertEdgeCacheable(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;
        Assert.NotNull(cache);
        Assert.True(cache!.Public, $"expected public, got {cache}");
        Assert.Equal(TimeSpan.FromMinutes(5), cache.MaxAge);
    }

    private static void AssertNoPrivateData(string body)
    {
        foreach (var marker in new[]
                 {
                     FilamentNotesMarker, PurchaseNotesMarker, UsageNotesMarker, PurchasePriceMarker,
                     ExternalIdMarker, EmailMarker, "moonraker",
                 })
        {
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Prints ----

    [Fact]
    public async Task PublicPrint_ReturnsItsMetadata()
    {
        var owner = await SeedUserAsync();
        var printId = await SeedPrintAsync(owner, notes: "Printed in   two\ncolors with a 0.4 nozzle.");
        await UploadPrintImageAsync(owner, printId);

        var (response, body, seo) = await GetSeoAsync($"/api/Prints/{printId}/seo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEdgeCacheable(response);
        Assert.Equal("print", seo!.Type);
        Assert.Equal("Benchy | 3D Print Log", seo.Title);
        Assert.Equal("Printed in two colors with a 0.4 nozzle.", seo.Description);
        Assert.Equal($"{Web}/prints/{printId}", seo.CanonicalUrl);

        Assert.NotNull(seo.Image);
        Assert.StartsWith($"http://localhost/api/Prints/{printId}/social-image?v=", seo.Image!.Url, StringComparison.Ordinal);
        Assert.Equal(1200, seo.Image.Width);
        Assert.Equal(750, seo.Image.Height);
        Assert.Equal("Benchy", seo.Image.Alt);

        Assert.Equal("Ada Maker", seo.Author!.Name);
        Assert.Equal($"{Web}/users/{owner.User.Id}", seo.Author.Url);
        Assert.NotNull(seo.PublishedAt);
        Assert.NotNull(seo.UpdatedAt);

        Assert.Equal(
            [
                ("Printer", "Prusa MK4"),
                ("Material", "Galaxy Black PLA (Black)"),
                ("Print time", "1h 30m"),
                ("Status", "Success"),
            ],
            seo.Facts!.Select(f => (f.Label, f.Value)).ToList());
        AssertNoPrivateData(body);
    }

    [Fact]
    public async Task PublicPrint_ImageUrl_ServesTheSocialImage()
    {
        var owner = await SeedUserAsync();
        var printId = await SeedPrintAsync(owner);
        await UploadPrintImageAsync(owner, printId);

        var (_, _, seo) = await GetSeoAsync($"/api/Prints/{printId}/seo");
        var image = await _client.GetAsync(seo!.Image!.Url, Ct);

        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        using var codec = SKCodec.Create(new MemoryStream(await image.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Equal(seo.Image.Width, codec!.Info.Width);
        Assert.Equal(seo.Image.Height, codec.Info.Height);
    }

    [Fact]
    public async Task PrintWithoutNotes_GetsAFactualDescription()
    {
        var owner = await SeedUserAsync();
        var printId = await SeedPrintAsync(owner);

        var (_, _, seo) = await GetSeoAsync($"/api/Prints/{printId}/seo");

        Assert.Equal("Benchy printed by Ada Maker on October 3, 2026", seo!.Description);
        Assert.Null(seo.Image);
    }

    [Fact]
    public async Task PrintByAnUnnamedUserWithAPrivateProfile_HasNoAuthorLink()
    {
        var owner = await SeedUserAsync(User.ProfileViewStatus.Private, displayName: null);
        var printId = await SeedPrintAsync(owner, title: null);

        var (_, _, seo) = await GetSeoAsync($"/api/Prints/{printId}/seo");

        Assert.Equal("Untitled print | 3D Print Log", seo!.Title);
        Assert.Equal("Untitled print printed by a 3D Print Log user on October 3, 2026", seo.Description);
        Assert.Null(seo.Author);
    }

    [Fact]
    public async Task NamedAuthorWithAPrivateProfile_IsNamedButNotLinked()
    {
        var owner = await SeedUserAsync(User.ProfileViewStatus.Private);
        var printId = await SeedPrintAsync(owner);

        var (_, _, seo) = await GetSeoAsync($"/api/Prints/{printId}/seo");

        Assert.Equal("Ada Maker", seo!.Author!.Name);
        Assert.Null(seo.Author.Url);
    }

    [Theory]
    [InlineData(PrintViewStatus.Private)]
    [InlineData(PrintViewStatus.Unlisted)]
    public async Task PrintThatIsNotPublic_Is404_EvenForItsOwner(PrintViewStatus viewStatus)
    {
        var owner = await SeedUserAsync();
        var printId = await SeedPrintAsync(owner, viewStatus);

        var (anonymous, _, _) = await GetSeoAsync($"/api/Prints/{printId}/seo");
        var (asOwner, _, _) = await GetSeoAsync($"/api/Prints/{printId}/seo", owner.User.OAuthUserId);

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asOwner.StatusCode);
        Assert.False(anonymous.Headers.CacheControl?.Public ?? false);
    }

    [Fact]
    public async Task UnknownPrint_Is404()
    {
        var (response, _, _) = await GetSeoAsync("/api/Prints/876543210/seo");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LongNotes_AreCutAtAWordToAbout155Characters()
    {
        var owner = await SeedUserAsync();
        var notes = string.Join(' ', Enumerable.Repeat("layered", 60));
        var printId = await SeedPrintAsync(owner, notes: notes);

        var (_, _, seo) = await GetSeoAsync($"/api/Prints/{printId}/seo");

        Assert.True(seo!.Description!.Length <= 155, $"{seo.Description.Length} characters");
        Assert.EndsWith("layered…", seo.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingTheDefaultImage_ChangesTheImageUrl()
    {
        var owner = await SeedUserAsync();
        var printId = await SeedPrintAsync(owner);
        await UploadPrintImageAsync(owner, printId);
        var before = (await GetSeoAsync($"/api/Prints/{printId}/seo")).Dto!.Image!.Url;

        await UploadPrintImageAsync(owner, printId, isDefault: true);
        var after = (await GetSeoAsync($"/api/Prints/{printId}/seo")).Dto!.Image!.Url;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task ImageWithoutASocialFile_UsesTheOriginalsSize()
    {
        var owner = await SeedUserAsync();
        var printId = await SeedPrintAsync(owner);
        var imageId = await UploadPrintImageAsync(owner, printId);
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var row = await context.PrintImages.SingleAsync(i => i.Id == imageId, Ct);
            row.SocialFileId = null;
            row.SocialWidth = null;
            row.SocialHeight = null;
            await context.SaveChangesAsync(Ct);
        }

        var (_, _, seo) = await GetSeoAsync($"/api/Prints/{printId}/seo");

        Assert.Equal(1600, seo!.Image!.Width);
        Assert.Equal(1000, seo.Image.Height);
    }

    // ---- Projects ----

    [Fact]
    public async Task PublicProject_ReturnsItsMetadata()
    {
        var owner = await SeedUserAsync();
        var projectId = await SeedProjectAsync(owner, description: "Drawers and trays for a small desk.");
        await SeedPrintAsync(owner, projectId: projectId);
        await SeedPrintAsync(owner, PrintViewStatus.Private, projectId: projectId);
        await UploadProjectImageAsync(owner, projectId);

        var (response, body, seo) = await GetSeoAsync($"/api/Projects/{projectId}/seo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEdgeCacheable(response);
        Assert.Equal("project", seo!.Type);
        Assert.Equal("Desk organizer | 3D Print Log", seo.Title);
        Assert.Equal("Drawers and trays for a small desk.", seo.Description);
        Assert.Equal($"{Web}/projects/{projectId}", seo.CanonicalUrl);
        Assert.StartsWith($"http://localhost/api/Projects/{projectId}/social-image?v=", seo.Image!.Url, StringComparison.Ordinal);
        Assert.Equal(800, seo.Image.Width);
        Assert.Equal(1200, seo.Image.Height);
        Assert.Equal("Ada Maker", seo.Author!.Name);
        // The project page counts every print in the project.
        Assert.Equal(
            [("Prints", "2"), ("Status", "In progress")],
            seo.Facts!.Select(f => (f.Label, f.Value)).ToList());
        AssertNoPrivateData(body);
    }

    [Fact]
    public async Task ProjectWithoutADescription_GetsAFactualOne()
    {
        var owner = await SeedUserAsync();
        var projectId = await SeedProjectAsync(owner);
        await SeedPrintAsync(owner, projectId: projectId);

        var (_, _, seo) = await GetSeoAsync($"/api/Projects/{projectId}/seo");

        Assert.Equal("Desk organizer, a 3D printing project by Ada Maker with 1 print", seo!.Description);
        Assert.Null(seo.Image);
    }

    [Theory]
    [InlineData(Project.ProjectViewStatus.Private)]
    [InlineData(Project.ProjectViewStatus.Unlisted)]
    public async Task ProjectThatIsNotPublic_Is404(Project.ProjectViewStatus viewStatus)
    {
        var owner = await SeedUserAsync();
        var projectId = await SeedProjectAsync(owner, viewStatus);

        var (anonymous, _, _) = await GetSeoAsync($"/api/Projects/{projectId}/seo");
        var (asOwner, _, _) = await GetSeoAsync($"/api/Projects/{projectId}/seo", owner.User.OAuthUserId);
        var (unknown, _, _) = await GetSeoAsync($"/api/Projects/{Guid.NewGuid()}/seo");

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---- Users ----

    [Fact]
    public async Task PublicProfile_ReturnsItsMetadata()
    {
        var owner = await SeedUserAsync(bio: "I print terrain for tabletop games.",
            profilePicture: "https://images.example.test/ada.png");
        await SeedPrintAsync(owner);
        await SeedPrintAsync(owner);
        await SeedPrintAsync(owner, PrintViewStatus.Unlisted);
        await SeedPrintAsync(owner, PrintViewStatus.Private);

        var (response, body, seo) = await GetSeoAsync($"/api/Users/{owner.User.Id}/seo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEdgeCacheable(response);
        Assert.Equal("profile", seo!.Type);
        Assert.Equal("Ada Maker Profile | 3D Print Log", seo.Title);
        Assert.Equal("I print terrain for tabletop games.", seo.Description);
        Assert.Equal($"{Web}/users/{owner.User.Id}", seo.CanonicalUrl);
        Assert.Equal("https://images.example.test/ada.png", seo.Image!.Url);
        Assert.Null(seo.Image.Width);
        Assert.Equal("Ada Maker", seo.Author!.Name);
        Assert.Equal(seo.CanonicalUrl, seo.Author.Url);
        Assert.Equal(new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero), seo.PublishedAt);
        Assert.Equal([("Public prints", "2")], seo.Facts!.Select(f => (f.Label, f.Value)).ToList());
        AssertNoPrivateData(body);
    }

    [Fact]
    public async Task ProfileWithoutABio_GetsTheSitesDescription()
    {
        var owner = await SeedUserAsync();

        var (_, _, seo) = await GetSeoAsync($"/api/Users/{owner.User.Id}/seo");

        Assert.Equal("View Ada Maker's 3D prints and projects on 3DPrintLog.com", seo!.Description);
        Assert.Null(seo.Image);
    }

    [Theory]
    [InlineData(User.ProfileViewStatus.Private)]
    [InlineData(User.ProfileViewStatus.Unlisted)]
    [InlineData(User.ProfileViewStatus.Friends)]
    public async Task ProfileThatIsNotPublic_Is404(User.ProfileViewStatus viewStatus)
    {
        var owner = await SeedUserAsync(viewStatus);

        var (anonymous, _, _) = await GetSeoAsync($"/api/Users/{owner.User.Id}/seo");
        var (asOwner, _, _) = await GetSeoAsync($"/api/Users/{owner.User.Id}/seo", owner.User.OAuthUserId);
        var (unknown, _, _) = await GetSeoAsync("/api/Users/876543210/seo");

        Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ProfileWithANonHttpPicture_HasNoImage()
    {
        var owner = await SeedUserAsync(profilePicture: "javascript:alert(1)");

        var (_, _, seo) = await GetSeoAsync($"/api/Users/{owner.User.Id}/seo");

        Assert.Null(seo!.Image);
    }
}
