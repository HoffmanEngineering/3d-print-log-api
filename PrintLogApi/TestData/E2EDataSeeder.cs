using System;
using System.Collections.Generic;
using PrintLogApi.Enums;
using PrintLogApi.Models;

namespace PrintLogApi.TestData
{
    public static class E2EDataSeeder
    {
        public static void Seed(PrintLogContext context)
        {
            var user = SeedUser(context);
            var printer = SeedPrinter(context, user.Id);
            var filaments = SeedFilaments(context, user.Id);
            var project = SeedProject(context, user.Id);
            SeedPrints(context, user.Id, printer.Id, filaments, project);
        }

        private static User SeedUser(PrintLogContext context)
        {
            var user = new User
            {
                OAuthUserId = "dev|1",
                ViewStatus = User.ProfileViewStatus.Public,
            };
            context.Users.Add(user);
            context.SaveChanges();
            return user;
        }

        private static Printer SeedPrinter(PrintLogContext context, long userId)
        {
            var printer = new Printer
            {
                Name = "Test Printer",
                Make = "TEVO",
                Model = "Tornado",
                UserId = userId,
                IsActive = true,
            };
            context.Printers.Add(printer);
            context.SaveChanges();
            return printer;
        }

        private static List<Filament> SeedFilaments(PrintLogContext context, long userId)
        {
            var filaments = new List<Filament>
            {
                new Filament
                {
                    MaterialCategoryNickname = "filament",
                    DisplayName = "Seed PLA Solid Red",
                    Brand = "E2E Seed",
                    MaterialType = "PLA",
                    MaterialDensityGramPerCubicCm = 1.24,
                    ColorName = "Seed Red",
                    ColorHex = "FF0000",
                    ColorPattern = ColorPatternType.Solid,
                    FinishType = FilamentFinishType.Standard,
                    Colors = new List<string> { "FF0000" },
                    Effects = new List<FilamentEffect>(),
                    DiameterMm = 1.75,
                    // Required: SourceMeasurement has no 0 member (Weight=1, Length=2,
                    // Volume=3), so leaving this at the CLR default persists an
                    // out-of-range value.
                    Source = Filament.SourceMeasurement.Weight,
                    InitialTotalWeightMg = 1000000,
                    InitialNominalWeightMg = 1000000,
                    SpoolWeightMg = 200000,
                    IsActive = true,
                    PurchasePriceValue = "20.00",
                    PurchasePriceCurrency = "USD",
                },
                new Filament
                {
                    MaterialCategoryNickname = "filament",
                    DisplayName = "Seed PETG Multi Silk",
                    Brand = "E2E Seed",
                    MaterialType = "PETG",
                    MaterialDensityGramPerCubicCm = 1.27,
                    ColorName = "Seed Tri-Color",
                    ColorHex = "00FF00",
                    ColorPattern = ColorPatternType.Multi,
                    FinishType = FilamentFinishType.Silk,
                    Colors = new List<string> { "00FF00", "0000FF", "FFFF00" },
                    Effects = new List<FilamentEffect>(),
                    DiameterMm = 1.75,
                    // Required: SourceMeasurement has no 0 member (Weight=1, Length=2,
                    // Volume=3), so leaving this at the CLR default persists an
                    // out-of-range value.
                    Source = Filament.SourceMeasurement.Weight,
                    InitialTotalWeightMg = 1000000,
                    InitialNominalWeightMg = 1000000,
                    SpoolWeightMg = 200000,
                    IsActive = true,
                    PurchasePriceValue = "28.00",
                    PurchasePriceCurrency = "USD",
                },
                new Filament
                {
                    MaterialCategoryNickname = "filament",
                    DisplayName = "Seed ABS Glow Matte",
                    Brand = "E2E Seed",
                    MaterialType = "ABS",
                    MaterialDensityGramPerCubicCm = 1.04,
                    ColorName = "Seed Glow Green",
                    ColorHex = "78FF78",
                    ColorPattern = ColorPatternType.Solid,
                    FinishType = FilamentFinishType.Matte,
                    Colors = new List<string> { "78FF78" },
                    Effects = new List<FilamentEffect> { FilamentEffect.GlowInDark },
                    DiameterMm = 1.75,
                    // Required: SourceMeasurement has no 0 member (Weight=1, Length=2,
                    // Volume=3), so leaving this at the CLR default persists an
                    // out-of-range value.
                    Source = Filament.SourceMeasurement.Weight,
                    InitialTotalWeightMg = 1000000,
                    InitialNominalWeightMg = 1000000,
                    SpoolWeightMg = 200000,
                    IsActive = true,
                    PurchasePriceValue = "32.00",
                    PurchasePriceCurrency = "USD",
                },
            };

            // Only the ownership ids are set here. PrintLogContext.SaveChanges calls
            // UpdateTimestamps(), which stamps CreatedDate and UpdatedDate on every
            // Added TimestampEntity — assigning them here would be silently discarded.
            foreach (var filament in filaments)
            {
                filament.CreatedById = userId;
                filament.UpdatedById = userId;
            }

            context.Filaments.AddRange(filaments);
            context.SaveChanges();
            return filaments;
        }

        private static Project SeedProject(PrintLogContext context, long userId)
        {
            var project = new Project
            {
                Name = "E2E Seed Project",
                Description = "Project seeded for the e2e suite.",
                Status = Project.ProjectStatus.InProgress,
                ViewStatus = Project.ProjectViewStatus.Public,
                CreatedById = userId,
                UpdatedById = userId,
                // CreatedDate/UpdatedDate are stamped by UpdateTimestamps() on save.
            };

            context.Projects.Add(project);
            context.SaveChanges();
            return project;
        }

        private static void SeedPrints(
            PrintLogContext context,
            long userId,
            long printerId,
            List<Filament> filaments,
            Project project)
        {
            var now = DateTimeOffset.UtcNow;
            var prints = new List<Print>();

            // The history below is carried by StartDate, not by CreatedDate. Every
            // analytics service filters on StartDate (see AnalyticsQueryScope), and
            // UpdateTimestamps() overwrites CreatedDate with "now" on save regardless,
            // so setting CreatedDate to a past date would achieve nothing.

            // Recent prints, inside the "Last 7 days" preset.
            for (int i = 1; i <= 3; i++)
            {
                prints.Add(new Print
                {
                    Title = $"Test Successful Print {i}",
                    Notes = $"E2E test print {i}",
                    StartDate = now.AddDays(-i),
                    Status = Print.PrintStatus.Success,
                    ViewStatus = Print.PrintViewStatus.Public,
                    AllowComments = true,
                    PrinterId = printerId,
                    CreatedById = userId,
                    UpdatedById = userId,
                    EstimatedPrintTimeInSeconds = 3600,
                    PrintTimeInSeconds = 3700,
                });
            }

            // Older prints, one per month, so wider date ranges are not a single bucket.
            for (int monthsAgo = 1; monthsAgo <= 8; monthsAgo++)
            {
                var start = now.AddMonths(-monthsAgo);
                prints.Add(new Print
                {
                    Title = $"Seed History Print {monthsAgo} months ago",
                    Notes = "E2E seeded history.",
                    StartDate = start,
                    Status = monthsAgo % 4 == 0 ? Print.PrintStatus.Failed : Print.PrintStatus.Success,
                    ViewStatus = Print.PrintViewStatus.Public,
                    AllowComments = true,
                    PrinterId = printerId,
                    CreatedById = userId,
                    UpdatedById = userId,
                    EstimatedPrintTimeInSeconds = 3600 * monthsAgo,
                    PrintTimeInSeconds = (3600 * monthsAgo) + 300,
                });
            }

            // One print carries the project, so the project chip renders in the list.
            prints[0].ProjectId = project.Id;

            context.Prints.AddRange(prints);
            context.SaveChanges();

            // One print carries filament usage, so the material swatch renders in the
            // print row and the filament-usage screens have a row to act on.
            // Note the DbSet is singular: `PrintFilament`, not `PrintFilaments`.
            context.PrintFilament.Add(new PrintFilament
            {
                PrintId = prints[0].Id,
                FilamentId = filaments[1].Id,
                AmountMg = 25000,
                EstimatedAmountMg = 24000,
                Source = PrintFilament.SourceMeasurement.Weight,
                EstimatedSource = PrintFilament.SourceMeasurement.Weight,
            });

            context.SaveChanges();
        }
    }
}
