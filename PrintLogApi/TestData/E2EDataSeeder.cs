using System;
using PrintLogApi.Models;

namespace PrintLogApi.TestData
{
    public static class E2EDataSeeder
    {
        public static void Seed(PrintLogContext context)
        {
            var user = SeedUser(context);
            var printer = SeedPrinter(context, user.Id);
            SeedPrints(context, user.Id, printer.Id);
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

        private static void SeedPrints(PrintLogContext context, long userId, long printerId)
        {
            var now = DateTimeOffset.UtcNow;

            for (int i = 1; i <= 3; i++)
            {
                context.Prints.Add(new Print
                {
                    Title = $"Test Successful Print {i}",
                    Notes = $"E2E test print {i}",
                    StartDate = now.AddDays(-i),
                    Status = Print.PrintStatus.Success,
                    ViewStatus = Print.PrintViewStatus.Public,
                    AllowComments = true,
                    PrinterId = printerId,
                    CreatedById = userId,
                    CreatedDate = now.UtcDateTime,
                    UpdatedById = userId,
                    UpdatedDate = now.UtcDateTime,
                    EstimatedPrintTimeInSeconds = 3600,
                });
            }

            context.SaveChanges();
        }
    }
}
