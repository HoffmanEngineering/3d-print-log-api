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
        public static long RichPrintId1 { get; private set; } // Printer2, Success, 25 g, 7200 s, Filament1
        public static long RichPrintId2 { get; private set; } // Printer2, Failed, 10 g, 3600 s, Filament2
        public static long ForeignPrintId { get; private set; } // owned by OtherUser, Public

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

            // Preferred currency (UserSettingType 5 = Currency_Name) for the primary user.
            context.UserSettings.Add(new UserSetting
            {
                UserId = primaryUserId,
                UserSettingTypeId = 5,
                Value = "GBP",
                CreatedById = primaryUserId,
                CreatedDate = now,
                UpdatedById = primaryUserId,
                UpdatedDate = now,
            });
            context.SaveChanges();
        }

        public const string PrimaryUserCurrency = "GBP";
    }
}
