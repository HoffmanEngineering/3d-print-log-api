using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Matching;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class TagSuggestionRankerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TagReading PinkPla() => new()
    {
        Brand = "Polymaker",
        MaterialType = "PLA",
        Colors = ["F72585"],
        NetWeightG = 1000,
    };

    private static async Task<long> AddUser(CatalogTestHost host)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User { OAuthUserId = $"auth0|{Guid.NewGuid()}" };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user.Id;
    }

    private static async Task<Guid> AddFilament(
        CatalogTestHost host,
        long userId,
        string? brand = "Polymaker",
        string? material = "PLA",
        string? color = "F72585",
        bool tagged = false,
        bool active = true,
        long? weightMg = 1_000_000,
        DateTime? updated = null)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var now = DateTime.UtcNow;
        var filament = new Filament
        {
            MaterialCategoryNickname = "filament",
            DisplayName = "Spool",
            Brand = brand,
            MaterialType = material,
            Colors = color == null ? [] : [color],
            IsActive = active,
            InitialNominalWeightMg = weightMg,
            CreatedById = userId,
            UpdatedById = userId,
            CreatedDate = now,
            UpdatedDate = now,
        };
        db.Filaments.Add(filament);
        await db.SaveChangesAsync(Ct);
        if (tagged)
        {
            db.FilamentTags.Add(new FilamentTag
            {
                FilamentId = filament.Id,
                UserId = userId,
                Uid = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                CreatedDate = now,
            });
            await db.SaveChangesAsync(Ct);
        }
        if (updated != null)
        {
            // SaveChanges stamps UpdatedDate itself, so set it directly.
            await db.Filaments.Where(f => f.Id == filament.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.UpdatedDate, updated.Value), Ct);
        }
        return filament.Id;
    }

    private static async Task<IReadOnlyList<TagSuggestion>> Rank(CatalogTestHost host, long userId, TagReading? reading)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TagSuggestionRanker>().RankAsync(userId, reading, Ct);
    }

    [Fact]
    public async Task NullReading_ReturnsEmpty()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        await AddFilament(host, userId);

        Assert.Empty(await Rank(host, userId, null));
    }

    [Fact]
    public async Task ReadingWithoutMaterial_ReturnsEmpty()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        await AddFilament(host, userId);
        var reading = PinkPla();
        reading.MaterialType = null;

        Assert.Empty(await Rank(host, userId, reading));
    }

    [Fact]
    public async Task OtherUsersFilament_NeverAppears()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        var otherId = await AddUser(host);
        await AddFilament(host, otherId);

        Assert.Empty(await Rank(host, userId, PinkPla()));
    }

    [Fact]
    public async Task InactiveFilament_NeverAppears()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        await AddFilament(host, userId, active: false);

        Assert.Empty(await Rank(host, userId, PinkPla()));
    }

    [Fact]
    public async Task Material_MatchesByPrefixInEitherDirection_AndNotOtherMaterials()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        var plain = await AddFilament(host, userId, material: "PLA");
        var plus = await AddFilament(host, userId, material: "PLA+");
        await AddFilament(host, userId, material: "PETG");

        var forPla = await Rank(host, userId, PinkPla());
        Assert.Equal(new[] { plain, plus }.Order(), forPla.Select(s => s.FilamentId).Order());

        var plusReading = PinkPla();
        plusReading.MaterialType = "PLA+";
        var forPlus = await Rank(host, userId, plusReading);
        Assert.Equal(new[] { plain, plus }.Order(), forPlus.Select(s => s.FilamentId).Order());
    }

    [Fact]
    public async Task UntaggedRanksAboveTagged_WithReasonsInOrder()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        var tagged = await AddFilament(host, userId, tagged: true);
        var untagged = await AddFilament(host, userId, tagged: false, weightMg: 750_000);

        var result = await Rank(host, userId, PinkPla());

        Assert.Equal(new[] { untagged, tagged }, result.Select(s => s.FilamentId));
        Assert.Equal(new[] { "same-material", "close-color", "same-brand", "untagged" }, result[0].Reasons);
        Assert.Equal(0.4 + 0.3 + 0.15 + 0.1, result[0].Score, 6);
    }

    [Fact]
    public async Task SameWeight_AddsReasonBeforeUntagged()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        await AddFilament(host, userId);

        var result = await Rank(host, userId, PinkPla());

        var only = Assert.Single(result);
        Assert.Equal(new[] { "same-material", "close-color", "same-brand", "same-weight", "untagged" }, only.Reasons);
        Assert.Equal(1.0, only.Score, 6);
    }

    [Fact]
    public async Task FarColor_IsNotCloseColor()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        var far = await AddFilament(host, userId, color: "000000", brand: null, weightMg: null);

        var result = await Rank(host, userId, PinkPla());

        var only = Assert.Single(result);
        Assert.Equal(far, only.FilamentId);
        Assert.Equal(new[] { "same-material", "untagged" }, only.Reasons);
    }

    [Fact]
    public async Task EmptyColors_UsesColorHex()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var now = DateTime.UtcNow;
            db.Filaments.Add(new Filament
            {
                MaterialCategoryNickname = "filament",
                Brand = "Other",
                MaterialType = "PLA",
                Colors = [],
                ColorHex = "F72585",
                IsActive = true,
                CreatedById = userId,
                UpdatedById = userId,
                CreatedDate = now,
                UpdatedDate = now,
            });
            await db.SaveChangesAsync(Ct);
        }

        var result = await Rank(host, userId, PinkPla());

        Assert.Contains("close-color", Assert.Single(result).Reasons);
    }

    [Fact]
    public async Task BrandMatch_UsesManufacturerToo_CaseInsensitive()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        await AddFilament(host, userId, brand: "polymaker", color: "000000", tagged: true);
        var reading = new TagReading { Brand = "Snapmaker", Manufacturer = "Polymaker", MaterialType = "PLA" };

        var result = await Rank(host, userId, reading);

        Assert.Equal(new[] { "same-material", "same-brand" }, Assert.Single(result).Reasons);
    }

    [Fact]
    public async Task BelowHalf_IsDropped()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        // Material only (tagged, wrong color and brand) scores 0.4.
        await AddFilament(host, userId, brand: "Other", color: "000000", tagged: true);

        Assert.Empty(await Rank(host, userId, PinkPla()));
    }

    [Fact]
    public async Task ReturnsAtMostFive()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        for (var i = 0; i < 7; i++)
        {
            await AddFilament(host, userId);
        }

        Assert.Equal(5, (await Rank(host, userId, PinkPla())).Count);
    }

    [Fact]
    public async Task Ties_GoToTheMostRecentlyUpdated()
    {
        await using var host = new CatalogTestHost();
        var userId = await AddUser(host);
        var newest = await AddFilament(host, userId, updated: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var oldest = await AddFilament(host, userId, updated: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = await AddFilament(host, userId, updated: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var result = await Rank(host, userId, PinkPla());

        Assert.Equal(new[] { newest, middle, oldest }, result.Select(s => s.FilamentId));
    }
}
