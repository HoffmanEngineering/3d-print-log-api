using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

/// <summary>
/// Seeds isolated users and prints for achievement tests. Each test creates its own user so the
/// metrics it asserts on never see rows from a sibling test sharing the factory's database.
/// </summary>
public static class AchievementTestData
{
    public static async Task<User> CreateUserAsync(PrintLogContext db)
    {
        var user = new User
        {
            OAuthUserId = $"auth0|achievements-{Guid.NewGuid():N}",
            ViewStatus = User.ProfileViewStatus.Public,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    public static async Task<Printer> AddPrinterAsync(PrintLogContext db, User user)
    {
        var printer = new Printer { Name = "Achievement Printer", UserId = user.Id, IsActive = true };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return printer;
    }

    /// <summary>
    /// Adds a print owned by <paramref name="user"/>, creating a printer when the user has none.
    /// <c>StartDate</c> defaults to now; <c>CreatedDate</c> is stamped by the context on save.
    /// </summary>
    public static async Task<Print> AddPrintAsync(PrintLogContext db, User user, Action<Print>? configure = null)
    {
        var printerId = await db.Printers.Where(p => p.UserId == user.Id).Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken)
            ?? (await AddPrinterAsync(db, user)).Id;

        var print = new Print
        {
            Title = "Achievement Print",
            PrinterId = printerId,
            CreatedById = user.Id,
            UpdatedById = user.Id,
            StartDate = DateTimeOffset.UtcNow,
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Private,
        };
        configure?.Invoke(print);
        db.Prints.Add(print);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return print;
    }

    /// <summary>
    /// Overwrites a print's audit timestamp. The context stamps <c>CreatedDate</c> on every save,
    /// so backdating has to bypass the change tracker.
    /// </summary>
    public static Task SetCreatedDateAsync(PrintLogContext db, long printId, DateTime createdUtc) =>
        db.Prints.Where(p => p.Id == printId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedDate, createdUtc), TestContext.Current.CancellationToken);
}
