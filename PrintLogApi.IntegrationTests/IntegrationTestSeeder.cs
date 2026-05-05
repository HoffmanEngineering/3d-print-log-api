using System;
using System.Collections.Generic;
using System.Linq;
using PrintLogApi.Models;

namespace PrintLogApi.IntegrationTests
{
    /// <summary>
    /// Lightweight test data seeder for integration tests.
    /// Creates minimal data needed to verify endpoints work correctly.
    /// </summary>
    public static class IntegrationTestSeeder
    {
        public const string TestUserOAuthId = "auth0|test-integration-user";

        // These are populated after seeding
        public static long TestUserId { get; private set; }
        public static long TestPrinterId { get; private set; }
        public static long TestPrinterId2 { get; private set; }
        public static long TestPrintId { get; private set; }
        public static int TestPrintImageId1 { get; private set; }
        public static int TestPrintImageId2 { get; private set; }
        public static Guid TestNotificationId1 { get; private set; }
        public static Guid TestNotificationId2 { get; private set; }
        public static Guid TestNotificationId3 { get; private set; }

        // Filament IDs (populated after seeding)
        public static Guid TestFilamentId1 { get; private set; } // Hatchbox Red PLA - StorageLocation: "Test Shelf"
        public static Guid TestFilamentId2 { get; private set; } // Prusament Blue PETG - StorageLocation: "Test Shelf"
        public static Guid TestFilamentId3 { get; private set; } // eSUN Black ABS - StorageLocation: null (unassigned)

        public const string TestStorageLocation = "Test Shelf";

        public static void Seed(PrintLogContext context)
        {
            var user = SeedUser(context);
            TestUserId = user.Id;

            var (printer, printer2) = SeedPrinter(context, user.Id);
            TestPrinterId = printer.Id;
            TestPrinterId2 = printer2.Id;

            var filamentIds = SeedFilaments(context, user.Id);
            TestFilamentId1 = filamentIds[0];
            TestFilamentId2 = filamentIds[1];
            TestFilamentId3 = filamentIds[2];

            var firstPrint = SeedPrints(context, user.Id, printer.Id);
            TestPrintId = firstPrint.Id;
            SeedPrintImages(context, firstPrint.Id, user.Id);
            SeedNotifications(context, user.Id);
        }

        private static User SeedUser(PrintLogContext context)
        {
            var user = new User
            {
                OAuthUserId = TestUserOAuthId,
                ViewStatus = User.ProfileViewStatus.Public
            };
            context.Users.Add(user);
            context.SaveChanges();
            return user;
        }

        private static (Printer, Printer) SeedPrinter(PrintLogContext context, long userId)
        {
            var printer = new Printer
            {
                Name = "Test Printer 1",
                Model = "Ender 3",
                Make = "Creality",
                UserId = userId,
                IsActive = true
            };
            context.Printers.Add(printer);

            var printer2 = new Printer
            {
                Name = "Test Printer 2",
                Model = "Prusa MK3S",
                Make = "Prusa",
                UserId = userId,
                IsActive = true
            };
            context.Printers.Add(printer2);

            context.SaveChanges();
            return (printer, printer2);
        }

        // Fixed GUIDs so the static properties are stable even when multiple test
        // classes seed in parallel (each has its own DB but shares these statics).
        private static readonly Guid _filament1Id = new Guid("aaaaaaaa-0001-0000-0000-000000000000");
        private static readonly Guid _filament2Id = new Guid("aaaaaaaa-0002-0000-0000-000000000000");
        private static readonly Guid _filament3Id = new Guid("aaaaaaaa-0003-0000-0000-000000000000");

        private static Guid[] SeedFilaments(PrintLogContext context, long userId)
        {
            var now = DateTime.UtcNow;

            var filament1Id = _filament1Id;
            var filament2Id = _filament2Id;
            var filament3Id = _filament3Id;

            context.Filaments.AddRange(new[]
            {
                new Filament
                {
                    Id = filament1Id,
                    Brand = "Hatchbox",
                    ColorHex = "FF0000",
                    ColorName = "Red",
                    CreatedById = userId,
                    CreatedDate = now,
                    UpdatedById = userId,
                    UpdatedDate = now,
                    DiameterMm = 1.75,
                    DisplayName = "Hatchbox Red PLA",
                    MaterialType = "PLA",
                    MaterialCategoryNickname = "filament",
                    MaterialDensityGramPerCubicCm = 1.24,
                    IsActive = true,
                    InitialNominalWeightMg = 1000000,
                    Source = Filament.SourceMeasurement.Weight,
                    StorageLocation = TestStorageLocation
                },
                new Filament
                {
                    Id = filament2Id,
                    Brand = "Prusament",
                    ColorHex = "0000FF",
                    ColorName = "Blue",
                    CreatedById = userId,
                    CreatedDate = now,
                    UpdatedById = userId,
                    UpdatedDate = now,
                    DiameterMm = 1.75,
                    DisplayName = "Prusament Blue PETG",
                    MaterialType = "PETG",
                    MaterialCategoryNickname = "filament",
                    MaterialDensityGramPerCubicCm = 1.27,
                    IsActive = true,
                    InitialNominalWeightMg = 1000000,
                    Source = Filament.SourceMeasurement.Weight,
                    StorageLocation = TestStorageLocation
                },
                new Filament
                {
                    Id = filament3Id,
                    Brand = "eSUN",
                    ColorHex = "000000",
                    ColorName = "Black",
                    CreatedById = userId,
                    CreatedDate = now,
                    UpdatedById = userId,
                    UpdatedDate = now,
                    DiameterMm = 1.75,
                    DisplayName = "eSUN Black ABS",
                    MaterialType = "ABS",
                    // Intentionally no MaterialCategoryNickname - used to test null-guard in weight computation
                    IsActive = true,
                    InitialNominalWeightMg = 1000000,
                    Source = Filament.SourceMeasurement.Weight
                }
            });
            context.SaveChanges();

            return new[] { filament1Id, filament2Id, filament3Id };
        }

        private static Print SeedPrints(PrintLogContext context, long userId, long printerId)
        {
            var now = DateTime.UtcNow;
            var baseDate = DateTimeOffset.UtcNow.AddDays(-7);

            Print firstPrint = null;
            for (int i = 1; i <= 5; i++)
            {
                var print = new Print
                {
                    Title = $"Test Print {i}",
                    Notes = $"Integration test print number {i}",
                    StartDate = baseDate.AddDays(i),
                    Status = i % 2 == 0 ? Print.PrintStatus.Success : Print.PrintStatus.Printing,
                    ViewStatus = Print.PrintViewStatus.Public,
                    AllowComments = true,
                    PrinterId = printerId,
                    CreatedById = userId,
                    CreatedDate = now,
                    UpdatedById = userId,
                    UpdatedDate = now,
                    EstimatedPrintTimeInSeconds = 3600 * i
                };
                context.Prints.Add(print);
                if (i == 1) firstPrint = print;
            }

            context.SaveChanges();

            // Verify prints were saved
            var savedCount = context.Prints.Count();
            if (savedCount != 5)
            {
                throw new Exception($"Expected 5 prints to be seeded, but found {savedCount}");
            }

            return firstPrint;
        }

        private static void SeedPrintImages(PrintLogContext context, long printId, long userId)
        {
            var now = DateTime.UtcNow;

            var file1 = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/test-image-1.jpg",
                Size = 1024,
                CreatedById = userId,
                UpdatedById = userId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var file2 = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = "printimages/test-image-2.jpg",
                Size = 2048,
                CreatedById = userId,
                UpdatedById = userId,
                CreatedDate = now,
                UpdatedDate = now
            };
            context.Files.AddRange(file1, file2);

            var image1 = new PrintImage
            {
                PrintId = printId,
                File = file1,
                IsDefault = true,
                DisplayOrder = 0,
                CreatedById = userId,
                UpdatedById = userId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var image2 = new PrintImage
            {
                PrintId = printId,
                File = file2,
                IsDefault = false,
                DisplayOrder = 1,
                CreatedById = userId,
                UpdatedById = userId,
                CreatedDate = now,
                UpdatedDate = now
            };
            context.PrintImages.AddRange(image1, image2);
            context.SaveChanges();

            TestPrintImageId1 = image1.Id;
            TestPrintImageId2 = image2.Id;
        }

        private static void SeedNotifications(PrintLogContext context, long userId)
        {
            var now = DateTime.UtcNow;

            var notification1 = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Type = NotificationType.PrintCompleted,
                Title = "Print Completed",
                Message = "Your print 'Test Print 1' has completed successfully.",
                IsRead = false,
                CreatedDate = now.AddHours(-1),
                ActionUrl = "/prints/1"
            };
            context.Notifications.Add(notification1);

            var notification2 = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Type = NotificationType.PrintFailed,
                Title = "Print Failed",
                Message = "Your print 'Test Print 2' has failed.",
                IsRead = false,
                CreatedDate = now.AddHours(-2),
                ActionUrl = "/prints/2"
            };
            context.Notifications.Add(notification2);

            var notification3 = new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Type = NotificationType.SystemAnnouncement,
                Title = "Welcome!",
                Message = "Welcome to 3D Print Log.",
                IsRead = true,
                CreatedDate = now.AddDays(-1),
                ReadDate = now.AddHours(-12)
            };
            context.Notifications.Add(notification3);

            context.SaveChanges();

            TestNotificationId1 = notification1.Id;
            TestNotificationId2 = notification2.Id;
            TestNotificationId3 = notification3.Id;
        }
    }
}
