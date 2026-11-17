using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Catalog;
using PrintLogApi.SpoolTags.Matching;
using Xunit;

namespace PrintLogApi.IntegrationTests.SpoolTags;

// Imports through the importer, which keeps its last ETag in a static field.
[Collection("SpoolTagCatalogImport")]
public class FilamentCatalogMatcherTests
{
    private const string BlueId = "polymaker_pla_panchroma(formerlypolylite)luminousblue_1000_175_n";
    private const string SilkBlackId = "polymaker_pla_panchroma(formerlypolylite)silkblack_1000_175_c";
    private const string OrangeId = "anycubic_pla_basicorange_1000_175_p";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CatalogTestHost> HostWithCatalog()
    {
        var host = new CatalogTestHost();
        host.Stub.Serve(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "spool-tags", "spoolmandb-sample.json")));
        await using var scope = host.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FilamentCatalogImporter>().ImportAsync(Ct);
        return host;
    }

    private static async Task<CatalogMatch?> Match(CatalogTestHost host, TagReading reading)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FilamentCatalogMatcher>().MatchAsync(reading, Ct);
    }

    private static async Task<string?> Trusted(CatalogTestHost host, string format, string sku)
    {
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FilamentCatalogMatcher>().TrustedEntryIdAsync(format, sku, Ct);
    }

    private static async Task Remove(CatalogTestHost host, string id)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var entry = await db.FilamentCatalogEntries.SingleAsync(e => e.Id == id, Ct);
        entry.RemovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Seeds one new user per vote. Each tuple is an entry id and how many users vote for it.</summary>
    private static async Task Vote(CatalogTestHost host, string format, string sku, params (string EntryId, int Voters)[] votes)
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        foreach (var (entryId, voters) in votes)
        {
            for (var i = 0; i < voters; i++)
            {
                var user = new User { OAuthUserId = $"auth0|{Guid.NewGuid()}" };
                db.Users.Add(user);
                await db.SaveChangesAsync(Ct);
                db.FilamentCatalogSkus.Add(new FilamentCatalogSku
                {
                    Format = format,
                    Sku = sku,
                    CatalogEntryId = entryId,
                    UserId = user.Id,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(Ct);
    }

    private static TagReading PolymakerPla(string color = "419EC2") => new()
    {
        Format = "snapmaker",
        Brand = "Snapmaker",
        Manufacturer = "Polymaker",
        MaterialType = "PLA",
        DiameterMm = 1.75,
        NetWeightG = 1000,
        Colors = [color],
    };

    [Fact]
    public async Task TrustedSku_MatchesWithFullConfidence_WhateverTheColor()
    {
        await using var host = await HostWithCatalog();
        await Vote(host, "snapmaker", "900001", (BlueId, 2));
        var reading = new TagReading { Format = "snapmaker", Sku = "900001", Colors = ["FF0000"], MaterialType = "ABS" };

        var match = await Match(host, reading);

        Assert.NotNull(match);
        Assert.Equal(BlueId, match.Id);
        Assert.Equal(1.0, match.Confidence);
    }

    [Fact]
    public async Task OneVote_IsNotTrusted()
    {
        await using var host = await HostWithCatalog();
        await Vote(host, "snapmaker", "900001", (BlueId, 1));

        Assert.Null(await Trusted(host, "snapmaker", "900001"));
    }

    [Fact]
    public async Task TiedVotes_AreNotTrusted()
    {
        await using var host = await HostWithCatalog();
        await Vote(host, "snapmaker", "900001", (BlueId, 2), (SilkBlackId, 2));

        Assert.Null(await Trusted(host, "snapmaker", "900001"));
    }

    [Fact]
    public async Task MoreVotesThanTheRunnerUp_IsTrusted()
    {
        await using var host = await HostWithCatalog();
        await Vote(host, "snapmaker", "900001", (BlueId, 3), (SilkBlackId, 2));

        Assert.Equal(BlueId, await Trusted(host, "snapmaker", "900001"));
    }

    [Fact]
    public async Task TrustedEntry_ThatWasRemoved_IsNotReturned()
    {
        await using var host = await HostWithCatalog();
        await Vote(host, "snapmaker", "900001", (BlueId, 2));
        await Remove(host, BlueId);

        Assert.Null(await Trusted(host, "snapmaker", "900001"));
    }

    [Fact]
    public async Task Heuristic_MatchesPolymakerPla_ByManufacturerBeforeBrand()
    {
        await using var host = await HostWithCatalog();

        // 4FA0C4 is about 14 from the Luminous Blue entry (419EC2). The weight differs from the
        // entry's, so only the color term lifts the score above the 0.5 base.
        var reading = PolymakerPla("4FA0C4");
        reading.NetWeightG = 750;
        var match = await Match(host, reading);
        var closeness = ColorDistance.Closeness(ColorDistance.Rgb("4FA0C4", "419EC2"));

        Assert.NotNull(match);
        Assert.Equal(BlueId, match.Id);
        Assert.True(match.Confidence > FilamentCatalogMatcher.MatchThreshold);
        Assert.Equal(0.5 + (0.3 * closeness), match.Confidence, 6);
        Assert.Equal("Polymaker", match.Manufacturer);
        Assert.Equal(140, match.SpoolWeightG);
        Assert.Equal(match.Name, match.ColorName);
        Assert.Equal(1.24, match.Density);
    }

    [Fact]
    public async Task ManufacturerMatch_IsCaseInsensitive()
    {
        await using var host = await HostWithCatalog();
        var reading = new TagReading { Manufacturer = "anycubic", MaterialType = "pla", Colors = ["FF7F32"], NetWeightG = 1000 };

        Assert.Equal(OrangeId, (await Match(host, reading))?.Id);
    }

    [Fact]
    public async Task Brand_IsUsed_WhenThereIsNoManufacturer()
    {
        await using var host = await HostWithCatalog();
        var reading = new TagReading { Brand = "Polymaker", MaterialType = "PLA", Colors = ["363538"], NetWeightG = 1000 };

        Assert.Equal(SilkBlackId, (await Match(host, reading))?.Id);
    }

    [Fact]
    public async Task Petg_NeverMatchesAPlaEntry_AndPlaNeverMatchesPetg()
    {
        await using var host = await HostWithCatalog();
        const string petgId = "polymaker_petg_polylitepetgblack_1000_175_c";

        // The fixture has a Polymaker PETG entry in black (101820). A PLA reading with that exact
        // color would score highest on it if the material were not checked.
        var pla = PolymakerPla("101820");
        var petg = PolymakerPla("101820");
        petg.MaterialType = "PETG";

        Assert.NotEqual(petgId, (await Match(host, pla))?.Id);
        Assert.Equal(petgId, (await Match(host, petg))?.Id);
        // The entry's own density, so a PETG spool doesn't inherit PLA's.
        Assert.Equal(1.25, (await Match(host, petg))?.Density);
        // And the PETG reading never lands on a PLA entry.
        Assert.NotEqual(BlueId, (await Match(host, petg))?.Id);
        Assert.NotEqual(SilkBlackId, (await Match(host, petg))?.Id);
    }

    [Fact]
    public async Task PlaPlus_DoesNotMatchPla()
    {
        await using var host = await HostWithCatalog();
        var reading = PolymakerPla();
        reading.MaterialType = "PLA+";

        Assert.Null(await Match(host, reading));
    }

    [Fact]
    public void MaterialKey_IgnoresCaseSpacesDashesAndUnderscores()
    {
        Assert.Equal("PETGCF", MaterialKey.Normalize("petg-cf"));
        Assert.Equal("PETGCF", MaterialKey.Normalize("PETG _CF"));
        Assert.Equal("PLA+", MaterialKey.Normalize("pla +"));
        Assert.Equal("", MaterialKey.Normalize(null));
    }

    [Fact]
    public async Task WrongDiameter_DoesNotMatch()
    {
        await using var host = await HostWithCatalog();
        var reading = PolymakerPla();
        reading.DiameterMm = 2.85;

        Assert.Null(await Match(host, reading));
    }

    [Fact]
    public async Task NoManufacturerOrBrand_NeverMatches()
    {
        await using var host = await HostWithCatalog();
        var reading = PolymakerPla();
        reading.Manufacturer = null;
        reading.Brand = null;

        Assert.Null(await Match(host, reading));
    }

    [Fact]
    public async Task RemovedEntries_NeverMatch()
    {
        await using var host = await HostWithCatalog();
        await Remove(host, BlueId);

        Assert.Null(await Match(host, PolymakerPla()));
    }

    [Fact]
    public async Task ColorFarAway_ScoresHalf_AndDoesNotMatch_EvenAsTheOnlyEntry()
    {
        await using var host = await HostWithCatalog();
        // AzureFilm's only entry is PETG 013C02. 01D202 is exactly 150 away in green; no subtype,
        // and a net weight different from the entry's.
        var reading = new TagReading { Manufacturer = "AzureFilm", MaterialType = "PETG", NetWeightG = 750, Colors = ["01D202"] };

        Assert.Equal(150, ColorDistance.Rgb("01D202", "013C02"));
        Assert.Null(await Match(host, reading));
    }

    [Fact]
    public async Task SameBrandMaterialSubtypeAndWeight_ButFarColor_DoesNotMatch()
    {
        await using var host = await HostWithCatalog();
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.FilamentCatalogEntries.Add(new FilamentCatalogEntry
            {
                Id = "farco_pla_matteblue",
                Manufacturer = "Farco",
                Name = "Matte Blue",
                Material = "PLA",
                WeightG = 1000,
                DiameterMm = 1.75,
                ColorHex = "0000FF",
                ImportedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }
        var reading = new TagReading
        {
            Manufacturer = "Farco",
            MaterialType = "PLA",
            MaterialSubtype = "Matte",
            NetWeightG = 1000,
            Colors = ["FF0000"],
        };

        Assert.Null(await Match(host, reading));

        // The same reading in the entry's own color still matches.
        reading.Colors = ["0000FF"];
        Assert.Equal("farco_pla_matteblue", (await Match(host, reading))?.Id);
    }

    [Fact]
    public async Task MultiColorReading_ComparesItsFirstColorToTheEntry()
    {
        await using var host = await HostWithCatalog();
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.FilamentCatalogEntries.Add(new FilamentCatalogEntry
            {
                Id = "farco_pla_duo",
                Manufacturer = "Farco",
                Name = "Duo Matte",
                Material = "PLA",
                WeightG = 1000,
                DiameterMm = 1.75,
                ColorHex = "FF0000",
                ColorHexesJson = "[\"FF0000\",\"0000FF\"]",
                ImportedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }
        var reading = new TagReading
        {
            Manufacturer = "Farco",
            MaterialType = "PLA",
            MaterialSubtype = "Matte",
            NetWeightG = 1000,
            Colors = ["FF0000", "0000FF"],
        };

        Assert.Equal("farco_pla_duo", (await Match(host, reading))?.Id);
    }

    [Fact]
    public async Task Ties_AreBrokenByIdOrdinal()
    {
        await using var host = await HostWithCatalog();
        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            foreach (var id in new[] { "zz_twin", "aa_twin" })
            {
                db.FilamentCatalogEntries.Add(new FilamentCatalogEntry
                {
                    Id = id,
                    Manufacturer = "Twinco",
                    Name = "Twin",
                    Material = "PLA",
                    WeightG = 1000,
                    DiameterMm = 1.75,
                    ColorHex = "112233",
                    ImportedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync(Ct);
        }
        var reading = new TagReading { Manufacturer = "TWINCO", MaterialType = "PLA", NetWeightG = 1000, Colors = ["112233"] };

        Assert.Equal("aa_twin", (await Match(host, reading))?.Id);
    }

    [Fact]
    public void ColorDistance_HandlesMissingAndMalformedColors()
    {
        Assert.Null(ColorDistance.Rgb(null, "FFFFFF"));
        Assert.Null(ColorDistance.Rgb("FFFFFF", "zzzzzz"));
        Assert.Equal(0, ColorDistance.Rgb("#ffffff", "FFFFFF"));
        Assert.Equal(Math.Sqrt(3) * 255, ColorDistance.Rgb("000000", "FFFFFF")!.Value, 6);
        Assert.Equal(0, ColorDistance.Closeness(null));
        Assert.Equal(0, ColorDistance.Closeness(250));
        Assert.Equal(0.5, ColorDistance.Closeness(50));
    }
}
