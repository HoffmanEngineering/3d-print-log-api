using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

/// <summary>
/// A heavy user for query-shape and timing checks: 10,000 prints over three printers and about 50
/// spools, a year of history. Seed through a context the achievement tracker has suppressed, or
/// the seeding itself evaluates.
/// </summary>
public static class AchievementLargeUserSeed
{
    public const int PrintCount = 10_000;

    public static async Task<User> SeedAsync(PrintLogContext db)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = new User { OAuthUserId = $"auth0|achievements-large-{Guid.NewGuid():N}", ViewStatus = User.ProfileViewStatus.Public };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var printers = Enumerable.Range(1, 3).Select(i => new Printer { Name = $"Farm {i}", UserId = user.Id, IsActive = true }).ToList();
        db.Printers.AddRange(printers);
        var spools = Enumerable.Range(1, 50).Select(i => new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = $"Spool {i}",
            MaterialType = (i % 5) switch { 0 => "PLA", 1 => "PETG", 2 => "ABS", 3 => "TPU", _ => "ASA" },
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        }).ToList();
        db.Filaments.AddRange(spools);
        await db.SaveChangesAsync(ct);

        var start = DateTimeOffset.UtcNow.AddDays(-365);
        for (var batch = 0; batch < PrintCount / 1000; batch++)
        {
            for (var i = 0; i < 1000; i++)
            {
                var n = batch * 1000 + i;
                var print = new Print
                {
                    Title = $"Print {n}",
                    PrinterId = printers[n % 3].Id,
                    CreatedById = user.Id,
                    UpdatedById = user.Id,
                    StartDate = start.AddMinutes(n * 52),
                    Status = n % 17 == 0 ? Print.PrintStatus.Failed : Print.PrintStatus.Success,
                    ViewStatus = n % 9 == 0 ? Print.PrintViewStatus.Public : Print.PrintViewStatus.Private,
                    PrintTimeInSeconds = 1800 + n % 7200,
                    Source = n % 4 == 0 ? PrintSource.SlicerPlugin : PrintSource.Web,
                    Slicer = n % 4 == 0 ? (n % 8 == 0 ? "cura" : "orcaslicer") : null,
                    FilamentUsage =
                    [
                        new PrintFilament { FilamentId = spools[n % 50].Id, AmountMg = 12_000 + n % 900 },
                    ],
                };
                db.Prints.Add(print);
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        return user;
    }
}
