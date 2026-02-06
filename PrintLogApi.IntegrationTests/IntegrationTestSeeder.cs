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
        public static Guid TestNotificationId1 { get; private set; }
        public static Guid TestNotificationId2 { get; private set; }
        public static Guid TestNotificationId3 { get; private set; }

        public static void Seed(PrintLogContext context)
        {
            var user = SeedUser(context);
            TestUserId = user.Id;

            var printer = SeedPrinter(context, user.Id);
            TestPrinterId = printer.Id;

            SeedFilaments(context, user.Id);
            SeedPrints(context, user.Id, printer.Id);
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

        private static Printer SeedPrinter(PrintLogContext context, long userId)
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
            return printer;
        }

        private static void SeedFilaments(PrintLogContext context, long userId)
        {
            var now = DateTime.UtcNow;

            context.Filaments.AddRange(new[]
            {
                new Filament
                {
                    Id = Guid.NewGuid(),
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
                    IsActive = true,
                    InitialNominalWeightMg = 1000000,
                    Source = Filament.SourceMeasurement.Weight
                },
                new Filament
                {
                    Id = Guid.NewGuid(),
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
                    IsActive = true,
                    InitialNominalWeightMg = 1000000,
                    Source = Filament.SourceMeasurement.Weight
                },
                new Filament
                {
                    Id = Guid.NewGuid(),
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
                    IsActive = true,
                    InitialNominalWeightMg = 1000000,
                    Source = Filament.SourceMeasurement.Weight
                }
            });
            context.SaveChanges();
        }

        private static void SeedPrints(PrintLogContext context, long userId, long printerId)
        {
            var now = DateTime.UtcNow;
            var baseDate = DateTimeOffset.UtcNow.AddDays(-7);

            for (int i = 1; i <= 5; i++)
            {
                context.Prints.Add(new Print
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
                });
            }

            context.SaveChanges();

            // Verify prints were saved
            var savedCount = context.Prints.Count();
            if (savedCount != 5)
            {
                throw new Exception($"Expected 5 prints to be seeded, but found {savedCount}");
            }
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
