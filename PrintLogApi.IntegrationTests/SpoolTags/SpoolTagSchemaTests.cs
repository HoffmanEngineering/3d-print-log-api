using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.SpoolTags;

public class SpoolTagSchemaTests
{
    private static async Task<(SqliteConnection Connection, DbContextOptions<PrintLogContext> Options)> CreateDatabase(CancellationToken ct)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        SqliteSearchCollation.Register(connection);
        var options = new DbContextOptionsBuilder<PrintLogContext>().UseSqlite(connection).Options;
        await using var context = new PrintLogContext(options);
        await context.Database.EnsureCreatedAsync(ct);
        return (connection, options);
    }

    private static FilamentCatalogEntry NewEntry(string id) => new()
    {
        Id = id,
        Manufacturer = "Test",
        Name = "Black",
        Material = "PLA",
        Density = 1.24,
        WeightG = 1000,
        SpoolWeightG = 140,
        SpoolType = "plastic",
        DiameterMm = 1.75,
        ColorHex = "000000",
        ColorHexesJson = "[\"000000\"]",
        ExtruderTemp = 210,
        ExtruderTempMin = 190,
        ExtruderTempMax = 230,
        BedTemp = 60,
        BedTempMin = 50,
        BedTempMax = 70,
        Finish = "matte",
        Pattern = "marble",
        Translucent = true,
        Glow = true,
        ImportedAt = new DateTime(2026, 10, 16, 1, 2, 3, DateTimeKind.Utc),
        RemovedAt = new DateTime(2026, 10, 17, 1, 2, 3, DateTimeKind.Utc),
    };

    [Fact]
    public async Task RoundTrips_NewColumnsAndTables()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, options) = await CreateDatabase(ct);
        await using var _ = connection;

        var readAt = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var now = DateTime.UtcNow;
        long userId;
        Guid filamentId;
        await using (var context = new PrintLogContext(options))
        {
            var user = new User { OAuthUserId = $"auth0|schema-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
            context.Users.Add(user);
            await context.SaveChangesAsync(ct);
            userId = user.Id;

            context.FilamentCatalogEntries.Add(NewEntry("test_pla_black_1000_175_n"));
            var filament = new Filament
            {
                MaterialCategoryNickname = "filament",
                DisplayName = "Catalog linked",
                CatalogEntryId = "test_pla_black_1000_175_n",
                CreatedById = user.Id,
                UpdatedById = user.Id,
                CreatedDate = now,
                UpdatedDate = now,
            };
            context.Filaments.Add(filament);
            await context.SaveChangesAsync(ct);
            filamentId = filament.Id;

            context.FilamentTags.Add(new FilamentTag
            {
                FilamentId = filament.Id,
                UserId = user.Id,
                Uid = "04A23F11",
                Format = "snapmaker",
                DecodedJson = "{}",
                DecoderVersion = 1,
                LastReadAt = readAt,
                CreatedDate = now,
            });
            context.FilamentCatalogSkus.Add(new FilamentCatalogSku
            {
                Format = "snapmaker",
                Sku = "SKU-1",
                CatalogEntryId = "test_pla_black_1000_175_n",
                UserId = user.Id,
                CreatedAt = readAt,
            });
            context.TagReadFailures.Add(new TagReadFailure
            {
                CreatedAt = readAt,
                Uid = "04A23F11",
                Techs = "MifareClassic",
                MemoryKind = "classic",
                Data = [1, 2, 3],
                DetectedFormat = "snapmaker",
                Error = "bad block",
            });
            await context.SaveChangesAsync(ct);
        }

        await using var fresh = new PrintLogContext(options);

        var entry = await fresh.FilamentCatalogEntries.SingleAsync(ct);
        var expected = NewEntry("test_pla_black_1000_175_n");
        Assert.Equal(expected.Id, entry.Id);
        Assert.Equal("Test", entry.Manufacturer);
        Assert.Equal("Black", entry.Name);
        Assert.Equal("PLA", entry.Material);
        Assert.Equal(1.24, entry.Density);
        Assert.Equal(1000, entry.WeightG);
        Assert.Equal(140, entry.SpoolWeightG);
        Assert.Equal("plastic", entry.SpoolType);
        Assert.Equal(1.75, entry.DiameterMm);
        Assert.Equal("000000", entry.ColorHex);
        Assert.Equal("[\"000000\"]", entry.ColorHexesJson);
        Assert.Equal(210, entry.ExtruderTemp);
        Assert.Equal(190, entry.ExtruderTempMin);
        Assert.Equal(230, entry.ExtruderTempMax);
        Assert.Equal(60, entry.BedTemp);
        Assert.Equal(50, entry.BedTempMin);
        Assert.Equal(70, entry.BedTempMax);
        Assert.Equal("matte", entry.Finish);
        Assert.Equal("marble", entry.Pattern);
        Assert.True(entry.Translucent);
        Assert.True(entry.Glow);
        Assert.Equal(expected.ImportedAt, entry.ImportedAt);
        Assert.Equal(expected.RemovedAt, entry.RemovedAt);

        var filament2 = await fresh.Filaments.Include(f => f.CatalogEntry).SingleAsync(f => f.Id == filamentId, ct);
        Assert.Equal("test_pla_black_1000_175_n", filament2.CatalogEntryId);
        Assert.Equal("Black", filament2.CatalogEntry?.Name);

        var tag = await fresh.FilamentTags.SingleAsync(ct);
        Assert.Equal("snapmaker", tag.Format);
        Assert.Equal("{}", tag.DecodedJson);
        Assert.Equal(1, tag.DecoderVersion);
        Assert.Equal(readAt, tag.LastReadAt);

        var sku = await fresh.FilamentCatalogSkus.SingleAsync(ct);
        Assert.True(sku.Id > 0);
        Assert.Equal("snapmaker", sku.Format);
        Assert.Equal("SKU-1", sku.Sku);
        Assert.Equal("test_pla_black_1000_175_n", sku.CatalogEntryId);
        Assert.Equal(userId, sku.UserId);
        Assert.Equal(readAt, sku.CreatedAt);

        var failure = await fresh.TagReadFailures.SingleAsync(ct);
        Assert.True(failure.Id > 0);
        Assert.Equal(readAt, failure.CreatedAt);
        Assert.Equal("04A23F11", failure.Uid);
        Assert.Equal("MifareClassic", failure.Techs);
        Assert.Equal("classic", failure.MemoryKind);
        Assert.Equal(new byte[] { 1, 2, 3 }, failure.Data);
        Assert.Equal("snapmaker", failure.DetectedFormat);
        Assert.Equal("bad block", failure.Error);
    }

    [Fact]
    public async Task CatalogSku_IsUniquePerFormatSkuAndUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var (connection, options) = await CreateDatabase(ct);
        await using var _ = connection;

        await using var context = new PrintLogContext(options);
        var user = new User { OAuthUserId = $"auth0|sku-{Guid.NewGuid()}", ViewStatus = ProfileViewStatus.Public };
        context.Users.Add(user);
        context.FilamentCatalogEntries.AddRange(NewEntry("a"), NewEntry("b"));
        await context.SaveChangesAsync(ct);

        context.FilamentCatalogSkus.Add(new FilamentCatalogSku { Format = "snapmaker", Sku = "S1", CatalogEntryId = "a", UserId = user.Id, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync(ct);

        context.FilamentCatalogSkus.Add(new FilamentCatalogSku { Format = "snapmaker", Sku = "S1", CatalogEntryId = "b", UserId = user.Id, CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
    }
}
