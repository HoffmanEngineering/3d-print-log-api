using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.TestData
{
    public static class DataSeeder
    {
        public static void Seed(PrintLogContext context)
        {
            // Add Users
            var users = GetTestUsers();

            context.Users.AddRange(users);
            Save<User>(context);

            // Add Printer
            var printers = GetTestPrinters();

            context.Printers.AddRange(printers);
            Save<Printer>(context);

            // Create Test Filament

            var filament = GetTestFilament();

            context.Filaments.AddRange(filament);
            context.SaveChanges();

            // Add Prints
            var prints = GetTestPrints(filament);

            context.Prints.AddRange(prints);
            Save<Print>(context);


        }

        private static List<Filament> GetTestFilament()
        {
             List<Filament> testFilament = new List<Filament>()
             {

             };

            for (int i = 1; i <= 10000; i++)
            {
                var createdDate = new DateTime();
                var filament = new Filament()
                {
                    Brand = $"Test Brand {i % 10}",
                    ColorHex = "FFFFFF",
                    ColorName = "White",
                    CreatedById = 1,
                    CreatedDate = createdDate,
                    UpdatedById = 1,
                    UpdatedDate = createdDate,
                    DiameterMm = 1.75,
                    DisplayName = $"Filament {i}",
                    FilamentAdjustments = new List<FilamentAdjustment>(),
                    Id = new Guid(),
                    InitialNominalWeightMg = 1000000,
                    InitialTotalWeightMg = 1250000,
                    SpoolWeightMg = 250000,
                    IsActive = true,
                    IsFavorite = false,
                    MaterialDensityGramPerCubicCm = 2.54,
                    MaterialType = "PLA",
                    Notes = $"Test Notes for Filament {i}",
                    PurchasePriceValue = i % 2 == 0 ? "20.01" : "24.99",
                };

                testFilament.Add(filament);
                    }

                    return testFilament;
        }

        private static List<User> GetTestUsers()
        {
            List<User> testUsers = new List<User>()
            {
                new User()
                {
                    Id = 1,
                    OAuthUserId = "auth0|5eb0be4f1cc1ac0c1485bc3b", // Test1234@Test1234.com
                    ViewStatus = User.ProfileViewStatus.Public
                }
            };

            return testUsers;
        }

        private static List<Printer> GetTestPrinters()
        {
            List<Printer> testPrinters = new List<Printer>()
            {
                new Printer()
                {
                    Id = 1,
                    Name = "Test Printer",
                    Model = "Tornado",
                    Make = "TEVO",
                    UserId = 1,
                    IsActive = true
                }
            };

            return testPrinters;
        }

        private static List<Print> GetTestPrints(List<Filament> filaments)
        {
            List<Print> testPrinters = new List<Print>()
            {

            };

            for (int i = 1; i <= 10000; i++)
            {
                var createdDate = new DateTime();
                var print = new Print()
                {
                    Id = i,
                    StartDate = DateTimeOffset.Now.AddDays(-1),
                    Notes = "This is a Test Note for Print " + i + ".",
                    FilamentUsage = new List<PrintFilament>() {
                        new PrintFilament()
                        {
                            Filament = filaments[i-1],
                            EstimatedAmountMg = i,
                            IsEstimatedLengthSource = false,
                            IsActualLengthSource = false,
                            Notes = $"This is filament {i}"

                        }
                    },
                    EstimatedPrintTimeInSeconds = i,
                    CreatedById = 1,
                    CreatedDate = createdDate,
                    UpdatedById = 1,
                    UpdatedDate = createdDate,
                    PrinterId = 1,
                    Status = Print.PrintStatus.Success,
                    Title = "Test Successful Print" + i,
                    ViewStatus = Print.PrintViewStatus.Public,
                    AllowComments = true,
                    //Comments = new List<PrintComment>()
                    //{
                    //    new PrintComment
                    //    {
                    //        Id = i,
                    //        PrintId = i,
                    //        Comment = new Comment
                    //        {
                    //            Id = i,
                    //            Body = "Test Comment",
                    //            CreatedById = 1,
                    //            UpdatedById = 1,
                    //            CreatedDate= createdDate,
                    //            UpdatedDate = createdDate,
                    //        }
                    //    }
                    //}
                };

                testPrinters.Add(print);
            }

            return testPrinters;
        }

        private static void Save<TModel>(PrintLogContext context)
        {
            using var transaction = context.Database.BeginTransaction();
            context.Database.ExecuteSqlRaw("SET IDENTITY_INSERT " + context.Model.FindEntityType(typeof(TModel)).GetTableName() + " ON;");
            context.SaveChanges();
            context.Database.ExecuteSqlRaw("SET IDENTITY_INSERT " + context.Model.FindEntityType(typeof(TModel)).GetTableName() + " OFF;");
            transaction.Commit();
        }
    }
}
