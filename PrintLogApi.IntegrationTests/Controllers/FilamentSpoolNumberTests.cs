using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Filament;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The per-user integer spool number (#174). Spoolman clients address spools by integer id, so
/// every filament gets one: 1, 2, 3… per owner, assigned on create by every create path, never
/// settable by a client, and never handed out twice — an old QR label must not point at a
/// different spool after a delete.
///
/// Each test arranges a fresh user, because the numbers it asserts are that user's sequence and
/// the class fixture's database is shared with every sibling.
/// </summary>
public class FilamentSpoolNumberTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_AssignsOneThenTwo()
    {
        var user = await CreateUserAsync();

        var first = await CreateFilamentAsync(user);
        var second = await CreateFilamentAsync(user);

        Assert.Equal(1, first.SpoolNumber);
        Assert.Equal(2, second.SpoolNumber);
    }

    [Fact]
    public async Task Numbering_IsPerUser()
    {
        var alice = await CreateUserAsync();
        var bob = await CreateUserAsync();

        await CreateFilamentAsync(alice);
        await CreateFilamentAsync(alice);
        var bobsFirst = await CreateFilamentAsync(bob);

        Assert.Equal(1, bobsFirst.SpoolNumber);
    }

    [Fact]
    public async Task DeletedNumber_IsNotHandedOutAgain()
    {
        var user = await CreateUserAsync();
        await CreateFilamentAsync(user);
        var second = await CreateFilamentAsync(user);

        var delete = await SendAsync(HttpMethod.Delete, $"/api/Filaments/{second.Id}", user);
        Assert.True(delete.IsSuccessStatusCode, $"{(int)delete.StatusCode}");

        var third = await CreateFilamentAsync(user);

        // max + 1 would have been 2 again: the high-water mark is what makes it 3.
        Assert.Equal(3, third.SpoolNumber);
    }

    [Fact]
    public async Task ConcurrentCreates_GetDistinctNumbers()
    {
        var user = await CreateUserAsync();

        // Each create in its own scope, so they race through the database rather than tripping
        // EF's own one-operation-at-a-time guard on a shared DbContext. Not Task.Run: every
        // context here shares one in-memory SQLite connection, which is not thread-safe, so
        // these interleave at their awaits instead. That proves the logic, not SQL Server's
        // locking — SpoolNumberSqlServerTests runs the truly parallel version against SQL Server.
        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IFilamentService>();
            return await service.AddFilament(NewFilament(), user.Id);
        }));

        Assert.Equal(Enumerable.Range(1, 8), created.Select(f => f.SpoolNumber!.Value).Order());
    }

    [Fact]
    public async Task McpCreateMaterial_AssignsANumber_WithAndWithoutAnIdempotencyKey()
    {
        var user = await CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFilamentService>();
        var input = new MaterialAttributesInput
        {
            DisplayName = "MCP spool",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            DensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            Source = McpMeasurementSource.Weight,
            InitialAmount = 1000,
        };

        var plain = await service.CreateMaterialForMcp(user.Id, input, null, Ct);
        // The keyed path wraps the create in its own transaction; the number must come from
        // inside it rather than opening a second one.
        var keyed = await service.CreateMaterialForMcp(user.Id, input, $"key-{Guid.NewGuid():N}", Ct);

        var numbers = await SpoolNumbersAsync(user.Id, [plain.Material.Id, keyed.Material.Id]);
        Assert.Equal([1, 2], numbers);
    }

    [Fact]
    public async Task Put_CannotChangeTheNumber()
    {
        var user = await CreateUserAsync();
        var created = await CreateFilamentAsync(user);

        var detail = await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Get, $"/api/Filaments/{created.Id}", user));
        detail.SpoolNumber = 99;
        detail.DisplayName = "Renamed";
        var put = await SendAsync(HttpMethod.Put, $"/api/Filaments/{created.Id}", user, detail);
        Assert.True(put.IsSuccessStatusCode, $"{(int)put.StatusCode}: {await put.Content.ReadAsStringAsync(Ct)}");

        var after = await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Get, $"/api/Filaments/{created.Id}", user));
        Assert.Equal("Renamed", after.DisplayName);
        Assert.Equal(1, after.SpoolNumber);
    }

    [Fact]
    public async Task Summary_CarriesTheNumber()
    {
        var user = await CreateUserAsync();
        var created = await CreateFilamentAsync(user);

        var page = await ReadAsync<PagedList<FilamentSummaryDto>>(await SendAsync(HttpMethod.Get, "/api/Filaments", user));

        Assert.Equal(1, Assert.Single(page.Items!, f => f.Id == created.Id).SpoolNumber);
    }

    [Fact]
    public async Task ByNumber_FindsTheSpool()
    {
        var user = await CreateUserAsync();
        await CreateFilamentAsync(user);
        var second = await CreateFilamentAsync(user);

        var found = await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Get, "/api/Filaments/by-number/2", user));

        Assert.Equal(second.Id, found.Id);
        Assert.Equal(2, found.SpoolNumber);
    }

    [Fact]
    public async Task ByNumber_AnotherUsersNumber_Is404()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        await CreateFilamentAsync(owner);

        var response = await SendAsync(HttpMethod.Get, "/api/Filaments/by-number/1", stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ByNumber_UnknownNumber_Is404()
    {
        var user = await CreateUserAsync();

        var response = await SendAsync(HttpMethod.Get, "/api/Filaments/by-number/7", user);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A spool created while the migration ran — by the old app, which knows nothing about the
    /// column — has no number. It gets the next one the first time anything resolves by number,
    /// rather than staying unaddressable.
    /// </summary>
    [Fact]
    public async Task UnnumberedSpool_IsNumberedOnLookup()
    {
        var user = await CreateUserAsync();
        await CreateFilamentAsync(user);
        var straggler = await InsertUnnumberedFilamentAsync(user.Id);

        var found = await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Get, "/api/Filaments/by-number/2", user));

        Assert.Equal(straggler, found.Id);
        Assert.Equal(3, (await CreateFilamentAsync(user)).SpoolNumber);
    }

    private static AddFilamentDto NewFilament() => new()
    {
        DisplayName = $"Spool {Guid.NewGuid():N}",
        Brand = "Test Brand",
        MaterialType = "PLA",
        ColorHex = "00FF00",
        DiameterMm = 1.75,
        InitialNominalWeightMg = 1_000_000,
        MaterialDensityGramPerCubicCm = 1.24,
        IsActive = true,
    };

    private async Task<User> CreateUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|spool-number-{Guid.NewGuid():N}",
            DisplayName = "Spool numbers",
            ViewStatus = User.ProfileViewStatus.Private,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<Guid> InsertUnnumberedFilamentAsync(long userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var filament = new Filament
        {
            DisplayName = "Created by the old app",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            IsActive = true,
            CreatedById = userId,
            UpdatedById = userId,
        };
        db.Filaments.Add(filament);
        await db.SaveChangesAsync(Ct);
        return filament.Id;
    }

    private async Task<List<int>> SpoolNumbersAsync(long userId, Guid[] ids)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var numbers = await db.Filaments
            .Where(f => f.CreatedById == userId && ids.Contains(f.Id))
            .Select(f => f.SpoolNumber)
            .ToListAsync(Ct);
        return numbers.Select(n => n!.Value).Order().ToList();
    }

    private async Task<FilamentDetailDto> CreateFilamentAsync(User user) =>
        await ReadAsync<FilamentDetailDto>(await SendAsync(HttpMethod.Post, "/api/Filaments", user, NewFilament()));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, User user, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, Ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
