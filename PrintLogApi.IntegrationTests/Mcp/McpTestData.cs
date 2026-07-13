using System;
using System.Collections.Generic;
using PrintLogApi.Models;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// Supplements <see cref="IntegrationTestSeeder"/> with data the MCP tools need: a second
    /// user (for creator-only isolation) and prints carrying real filament-usage, duration, and
    /// per-filament links. Seeded into each MCP tool-test class's isolated in-memory database.
    /// </summary>
    public static class McpTestData
    {
        public const string OtherUserOAuthId = "auth0|mcp-other-user";

        public static long OtherUserId { get; private set; }
        public static long OtherPrinterId { get; private set; }
        public static Guid InactiveFilamentId { get; private set; } // primary user, inactive, null initial weight
        public static Guid NegativeFilamentId { get; private set; } // primary user, PLA/Crimson, remaining = -200 g
        public static long RichPrintId1 { get; private set; } // Printer2, Success, 25 g, 7200 s, Filament1
        public static long RichPrintId2 { get; private set; } // Printer2, Failed, 10 g, 3600 s, Filament2
        public static long ForeignPrintId { get; private set; } // owned by OtherUser, Public
        public static long SoapDishPrintId { get; private set; } // "Functional Soap Dish Bottom"
        public static long ProjectPrintId { get; private set; } // "Bracket", in project "Rocket Build"
        public static Guid ProjectId { get; private set; } // "Rocket Build"
        public static long SearchPrinterId { get; private set; } // holds the two undated search fixtures
        public static long AmsPrinterId { get; private set; } // carries more loaded spools than get_printer returns

        /// <summary>Loaded spools on <see cref="AmsPrinterId"/> — deliberately above the get_printer cap of 10.</summary>
        public const int AmsLoadedSpoolCount = 12;
        public static long DualColorPrintId { get; private set; } // 2 spools + orphan + zero-actual row
        public static long ForeignSpoolPrintId { get; private set; } // usage row pointing at another user's spool

        public static readonly DateTimeOffset RichPrint1Date = DateTimeOffset.UtcNow.AddDays(-1);
        public static readonly DateTimeOffset RichPrint2Date = DateTimeOffset.UtcNow;

        public static void Seed(PrintLogContext context)
        {
            var now = DateTime.UtcNow;
            var primaryUserId = IntegrationTestSeeder.TestUserId;

            var otherUser = new User
            {
                OAuthUserId = OtherUserOAuthId,
                ViewStatus = User.ProfileViewStatus.Public,
            };
            context.Users.Add(otherUser);
            context.SaveChanges();
            OtherUserId = otherUser.Id;

            var otherPrinter = new Printer
            {
                Name = "Other User Printer",
                Model = "X1C",
                Make = "Bambu",
                UserId = OtherUserId,
                IsActive = true,
            };
            context.Printers.Add(otherPrinter);
            context.SaveChanges();
            OtherPrinterId = otherPrinter.Id;

            var richPrint1 = new Print
            {
                Title = "Rich Print 1",
                Notes = "secret notes should never be exposed by search",
                StartDate = RichPrint1Date,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Public,
                PrinterId = IntegrationTestSeeder.TestPrinterId2,
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
                PrintTimeInSeconds = 7200,
                // Usage lives only in the per-filament rows (the scalar Print.FilamentUsageMg is
                // legacy and not maintained by the app), so MCP tools must read the child rows.
                FilamentUsage = new List<PrintFilament>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        FilamentId = IntegrationTestSeeder.TestFilamentId1,
                        AmountMg = 25000,
                    },
                },
            };

            var richPrint2 = new Print
            {
                Title = "Rich Print 2",
                StartDate = RichPrint2Date,
                Status = Print.PrintStatus.Failed,
                ViewStatus = Print.PrintViewStatus.Public,
                PrinterId = IntegrationTestSeeder.TestPrinterId2,
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
                PrintTimeInSeconds = 3600,
                FilamentUsage = new List<PrintFilament>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        FilamentId = IntegrationTestSeeder.TestFilamentId2,
                        AmountMg = 10000,
                    },
                },
            };

            var foreignPrint = new Print
            {
                Title = "FOREIGN PRINT",
                StartDate = RichPrint2Date,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Public,
                PrinterId = OtherPrinterId,
                CreatedById = OtherUserId,
                CreatedDate = now,
                UpdatedById = OtherUserId,
                UpdatedDate = now,
                PrintTimeInSeconds = 999,
                // Exercises the estimated-weight fallback (no actual AmountMg recorded).
                FilamentUsage = new List<PrintFilament>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        FilamentId = null,
                        AmountMg = null,
                        EstimatedAmountMg = 99000,
                    },
                },
            };

            context.Prints.AddRange(richPrint1, richPrint2, foreignPrint);
            context.SaveChanges();

            RichPrintId1 = richPrint1.Id;
            RichPrintId2 = richPrint2.Id;
            ForeignPrintId = foreignPrint.Id;

            var inactiveFilament = new Filament
            {
                Id = new Guid("aaaaaaaa-0004-0000-0000-000000000000"),
                Brand = "Overture",
                ColorHex = "00FF00",
                ColorName = "Green",
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
                DiameterMm = 1.75,
                DisplayName = "Overture Green TPU",
                MaterialType = "TPU",
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.21,
                IsActive = false,
                InitialNominalWeightMg = null, // exercises the null-remaining => 0 grams path
                Source = Filament.SourceMeasurement.Weight,
            };
            context.Filaments.Add(inactiveFilament);
            context.SaveChanges();
            InactiveFilamentId = inactiveFilament.Id;

            // Text-matching fixtures. These are REAL spellings taken from the production Filaments
            // table, where the material dropdown writes "ACRONYM (Full Name)" but ~6% of rows are
            // hand-typed. Exact matching finds only "PLA" here and misses the rest, which is the
            // bug McpTextMatch exists to fix. "PCTG" is the negative control: a "PC" query must not
            // match it.
            var textMatchFilaments = new List<Filament>
            {
                NewTextMatchFilament(
                    "aaaaaaaa-1001-0000-0000-000000000000", "Short PLA", "PLA", "Blue", primaryUserId, now),
                NewTextMatchFilament(
                    "aaaaaaaa-1002-0000-0000-000000000000", "Long PLA", "PLA (Polylactic Acid)", "Light Blue", primaryUserId, now),
                NewTextMatchFilament(
                    "aaaaaaaa-1003-0000-0000-000000000000", "Plus PLA", "PLA+", "Navy", primaryUserId, now),
                NewTextMatchFilament(
                    "aaaaaaaa-1004-0000-0000-000000000000", "Petg Roll", "PETG (Polyethylene Terephthalate Glycol)", "Red", primaryUserId, now),
                NewTextMatchFilament(
                    "aaaaaaaa-1005-0000-0000-000000000000", "Pctg Roll", "PCTG", "Black", primaryUserId, now),
            };
            context.Filaments.AddRange(textMatchFilaments);
            context.SaveChanges();

            // find_material fixtures, all PLA-family in Red so a single query exercises grouping.
            // Remaining weight == InitialNominalWeightMg when there is no usage or adjustment.
            var combinationBig = NewTextMatchFilament(
                "aaaaaaaa-2001-0000-0000-000000000000", "Crimson Big", "PLA", "Crimson", primaryUserId, now);
            combinationBig.InitialNominalWeightMg = 250_000; // 250 g

            var combinationSmall = NewTextMatchFilament(
                "aaaaaaaa-2002-0000-0000-000000000000", "Crimson Small", "PLA", "Crimson", primaryUserId, now);
            combinationSmall.InitialNominalWeightMg = 150_000; // 150 g

            // Same colour, DIFFERENT material. Must never be summed into the PLA group: 250 + 150 +
            // 500 would wrongly report a 600 g print as printable in plain PLA.
            var carbonFibre = NewTextMatchFilament(
                "aaaaaaaa-2003-0000-0000-000000000000", "Crimson CF", "PLA-CF", "Crimson", primaryUserId, now);
            carbonFibre.InitialNominalWeightMg = 500_000; // 500 g

            // Corrupt row: more logged as used than the spool ever held, so remaining goes negative.
            var negative = NewTextMatchFilament(
                "aaaaaaaa-2004-0000-0000-000000000000", "Crimson Corrupt", "PLA", "Crimson", primaryUserId, now);
            negative.InitialNominalWeightMg = 100_000; // 100 g

            context.Filaments.AddRange(combinationBig, combinationSmall, carbonFibre, negative);
            context.SaveChanges();

            context.FilamentAdjustments.Add(new FilamentAdjustment
            {
                FilamentId = negative.Id,
                AmountMg = -300_000, // drives remaining to -200 g
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
            });
            context.SaveChanges();

            NegativeFilamentId = negative.Id;

            // Text-search fixtures. "Functional Soap Dish Bottom" exercises partial-title search
            // ("soap dish"); the project lets a user find a print by the project they remember
            // rather than the print's own name.
            var project = new Project
            {
                // Fixed id: the other fixtures do the same. A DB-generated Guid would differ
                // between each test class's freshly seeded database, and these statics are shared.
                Id = new Guid("aaaaaaaa-3001-0000-0000-000000000000"),
                Name = "Rocket Build",
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
            };
            context.Projects.Add(project);
            context.SaveChanges();
            ProjectId = project.Id;

            // Dedicated printer, and StartDate left NULL. Both are deliberate: every ranged query
            // (summary, printer stats) excludes undated prints, and a separate printer keeps the
            // printer-filter and printer-stats fixtures untouched. These also double as the undated
            // prints needed to test all-time reconciliation.
            var searchPrinter = new Printer
            {
                Name = "Search Fixture Printer",
                Model = "SF1",
                Make = "Fixture",
                UserId = primaryUserId,
                IsActive = true,
            };
            context.Printers.Add(searchPrinter);
            context.SaveChanges();
            SearchPrinterId = searchPrinter.Id;

            var soapDish = new Print
            {
                Title = "Functional Soap Dish Bottom",
                StartDate = null,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = searchPrinter.Id,
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
                PrintTimeInSeconds = 1800,
            };

            var bracket = new Print
            {
                Title = "Bracket",
                StartDate = null,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = searchPrinter.Id,
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
                ProjectId = project.Id,
            };

            // Two colours on one print. Without a per-material breakdown, a print literally named
            // "Dual Color" cannot report which two colours it used.
            var dualColor = new Print
            {
                Title = "Dual Color 3D Benchy",
                StartDate = null,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = searchPrinter.Id,
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
                FilamentUsage = new List<PrintFilament>
                {
                    // Short PLA (PLA / Blue). Deliberately NOT a find_material fixture: consuming
                    // from those would change their remaining weight and break those tests.
                    new() { Id = Guid.NewGuid(), FilamentId = new Guid("aaaaaaaa-1001-0000-0000-000000000000"), AmountMg = 30000 },
                    // Plus PLA (PLA+ / Navy)
                    new() { Id = Guid.NewGuid(), FilamentId = new Guid("aaaaaaaa-1003-0000-0000-000000000000"), AmountMg = 20000 },
                    // Orphan row: FilamentId is nullable. An inner join would drop this and the
                    // per-material rows would stop summing to the print's total.
                    new() { Id = Guid.NewGuid(), FilamentId = null, AmountMg = null, EstimatedAmountMg = 10000 },
                    // Zero actual must fall through to the estimate, not be taken at face value.
                    new() { Id = Guid.NewGuid(), FilamentId = null, AmountMg = 0, EstimatedAmountMg = 4000 },
                },
            };

            context.Prints.AddRange(soapDish, bracket, dualColor);
            context.SaveChanges();

            SoapDishPrintId = soapDish.Id;
            ProjectPrintId = bracket.Id;
            DualColorPrintId = dualColor.Id;

            // Corrupt cross-owner reference: the caller's print points at ANOTHER user's spool.
            // Guarding only on "navigation is not null" would leak that user's brand/material/colour.
            var foreignSpool = NewTextMatchFilament(
                "aaaaaaaa-4001-0000-0000-000000000000", "OTHER USER SPOOL", "ABS", "Secret Purple", OtherUserId, now);
            // Inactive so it stays out of the other user's default inventory listing, keeping the
            // owner-isolation tests meaningful. Redaction is about get_print, not inventory.
            foreignSpool.IsActive = false;
            context.Filaments.Add(foreignSpool);
            context.SaveChanges();

            var leaky = new Print
            {
                Title = "Print With Foreign Spool",
                StartDate = null,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                PrinterId = searchPrinter.Id,
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
                FilamentUsage = new List<PrintFilament>
                {
                    new() { Id = Guid.NewGuid(), FilamentId = foreignSpool.Id, AmountMg = 7000 },
                },
            };
            context.Prints.Add(leaky);
            context.SaveChanges();
            ForeignSpoolPrintId = leaky.Id;

            // One spool currently loaded on the search printer, plus a historical row that has been
            // unloaded. Without the UnloadedDateTime filter, get_printer would report BOTH as
            // "loaded right now".
            // PrinterFilament has no DbSet on the context; it is reached through the Printer
            // navigation, so seed it via Set<T>().
            context.Set<PrinterFilament>().AddRange(
                new PrinterFilament
                {
                    Id = Guid.NewGuid(),
                    PrinterId = searchPrinter.Id,
                    FilamentId = new Guid("aaaaaaaa-1002-0000-0000-000000000000"), // Long PLA, Light Blue
                    LoadedDateTime = now,
                    UnloadedDateTime = null,
                },
                new PrinterFilament
                {
                    Id = Guid.NewGuid(),
                    PrinterId = searchPrinter.Id,
                    FilamentId = new Guid("aaaaaaaa-1003-0000-0000-000000000000"), // Plus PLA, Navy
                    LoadedDateTime = now.AddDays(-10),
                    UnloadedDateTime = now.AddDays(-5), // history, NOT loaded now
                },
                // Corrupt cross-owner row: another user's spool is recorded as loaded on the
                // caller's printer. Its material/colour/remaining all live on that foreign row, so
                // get_printer excludes it rather than emitting an entry with every field nulled -
                // but counts it, so the omission is visible rather than silent.
                new PrinterFilament
                {
                    Id = Guid.NewGuid(),
                    PrinterId = searchPrinter.Id,
                    FilamentId = foreignSpool.Id,
                    LoadedDateTime = now,
                    UnloadedDateTime = null,
                });
            context.SaveChanges();

            // An AMS/toolchanger printer carrying more spools than get_printer returns. Silently
            // dropping a spool from "what is loaded right now" is a WRONG answer, not merely an
            // incomplete one, so the cap must surface a true count and a truncation flag.
            // Material and colour are deliberately unique to these fixtures: reusing PLA or a blue
            // would change the counts the inventory and find_material tests assert.
            var amsPrinter = new Printer
            {
                Name = "AMS Fixture Printer",
                Model = "AMS1",
                Make = "Fixture",
                UserId = primaryUserId,
                IsActive = true,
            };
            context.Printers.Add(amsPrinter);
            context.SaveChanges();
            AmsPrinterId = amsPrinter.Id;

            var amsSpools = new List<Filament>();
            var amsLoads = new List<PrinterFilament>();
            for (var i = 0; i < AmsLoadedSpoolCount; i++)
            {
                var spool = NewTextMatchFilament(
                    $"aaaaaaaa-5{i:D3}-0000-0000-000000000000",
                    $"AMS Spool {i:D2}", "Nylon", "Amber", primaryUserId, now);
                amsSpools.Add(spool);

                amsLoads.Add(new PrinterFilament
                {
                    Id = Guid.NewGuid(),
                    PrinterId = amsPrinter.Id,
                    FilamentId = spool.Id,
                    // Distinct load times keep the "largest/most recent first" ordering
                    // deterministic, so which spools survive truncation is stable.
                    LoadedDateTime = now.AddMinutes(-i),
                    UnloadedDateTime = null,
                });
            }

            context.Filaments.AddRange(amsSpools);
            context.SaveChanges();
            // PrinterFilament has no DbSet on the context; it is reached through the Printer
            // navigation, so seed it via Set<T>().
            context.Set<PrinterFilament>().AddRange(amsLoads);
            context.SaveChanges();
        }

        private static Filament NewTextMatchFilament(
            string id, string displayName, string materialType, string colorName, long userId, DateTime now) =>
            new()
            {
                Id = new Guid(id),
                DisplayName = displayName,
                MaterialType = materialType,
                ColorName = colorName,
                Brand = "Fixture Brand",
                CreatedById = userId,
                CreatedDate = now,
                UpdatedById = userId,
                UpdatedDate = now,
                DiameterMm = 1.75,
                MaterialCategoryNickname = "filament",
                MaterialDensityGramPerCubicCm = 1.24,
                IsActive = true,
                InitialNominalWeightMg = 1_000_000,
                Source = Filament.SourceMeasurement.Weight,
            };
    }
}
