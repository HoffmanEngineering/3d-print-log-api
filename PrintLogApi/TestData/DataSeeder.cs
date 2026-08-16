using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;

namespace PrintLogApi.TestData;

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

        // Add Projects
        var projects = GetTestProjects();

        context.Projects.AddRange(projects);
        context.SaveChanges();

        // Add Prints
        var prints = GetTestPrints(filament, projects);

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
                Source = Filament.SourceMeasurement.Weight,
                InitialNominalLengthM = MeasurementUtilities.GetLengthInMetersFromAmount(1000000, 1.75, 2.54),
                InitialNominalVolumeMl = MeasurementUtilities.GetVolumeInMlFromAmount(1000000, 2.54),
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

    private static List<Project> GetTestProjects()
    {
        var projects = new List<Project>();
        var createdDate = new DateTime();

        for (int i = 1; i <= 500; i++)
        {
            projects.Add(new Project
            {
                Id = Guid.NewGuid(),
                Name = $"Test Project {i}",
                Description = $"Description for test project {i}",
                Status = (Project.ProjectStatus)((i % 4) + 1),
                ViewStatus = Project.ProjectViewStatus.Public,
                CreatedById = 1,
                CreatedDate = createdDate,
                UpdatedById = 1,
                UpdatedDate = createdDate,
            });
        }

        return projects;
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

    private static List<Print> GetTestPrints(List<Filament> filaments, List<Project> projects)
    {
        var prints = new List<Print>();
        int printId = 1;

        for (int p = 0; p < projects.Count; p++)
        {
            int printCount = (p % 10) + 1;
            for (int j = 0; j < printCount; j++)
            {
                prints.Add(MakePrint(printId, filaments, projectId: projects[p].Id));
                printId++;
            }
        }

        for (; printId <= 10000; printId++)
        {
            prints.Add(MakePrint(printId, filaments, projectId: null));
        }

        return prints;
    }

    private static Print MakePrint(int id, List<Filament> filaments, Guid? projectId)
    {
        var createdDate = new DateTime();
        return new Print()
        {
            Id = id,
            StartDate = DateTimeOffset.Now.AddDays(-1),
            Notes = $"This is a Test Note for Print {id}.",
            FilamentUsage = new List<PrintFilament>()
            {
                new PrintFilament()
                {
                    Filament = filaments[id - 1],
                    EstimatedAmountMg = id,
                    EstimatedSource = PrintFilament.SourceMeasurement.Weight,
                    Source = PrintFilament.SourceMeasurement.Weight,
                    Notes = $"This is filament {id}"
                }
            },
            EstimatedPrintTimeInSeconds = id,
            CreatedById = 1,
            CreatedDate = createdDate,
            UpdatedById = 1,
            UpdatedDate = createdDate,
            PrinterId = 1,
            Status = Print.PrintStatus.Success,
            Title = $"Test Successful Print {id}",
            ViewStatus = Print.PrintViewStatus.Public,
            AllowComments = true,
            ProjectId = projectId,
        };
    }

    private static void Save<TModel>(PrintLogContext context)
    {
        // Non-null for every current caller: Save<TModel> is only invoked with mapped entity
        // types. Nothing in the signature enforces that, so a future unmapped TModel returns
        // null here.
        var tableName = context.Model.FindEntityType(typeof(TModel))!.GetTableName();
        context.Database.CreateExecutionStrategy().Execute(() =>
        {
            using var transaction = context.Database.BeginTransaction();
            context.Database.ExecuteSql($"SET IDENTITY_INSERT {tableName} ON;");
            context.SaveChanges();
            context.Database.ExecuteSql($"SET IDENTITY_INSERT {tableName} OFF;");
            transaction.Commit();
        });
    }
}
