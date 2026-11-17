using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class AchievementSchemaTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AchievementSchemaTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static UserAchievement Row(long userId, string key = "first-print", int tier = 1) => new()
    {
        UserId = userId,
        AchievementKey = key,
        Tier = tier,
        UnlockedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task UniqueIndex_RejectsDuplicateUserKeyTier()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await AchievementTestData.CreateUserAsync(db);

        db.UserAchievements.Add(Row(user.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.UserAchievements.Add(Row(user.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cascade_DeletingUserDeletesAchievements()
    {
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await AchievementTestData.CreateUserAsync(db);
            userId = user.Id;
            db.UserAchievements.Add(Row(userId));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await db.UserAchievements.CountAsync(a => a.UserId == userId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UserDeletion_RemovesAchievements()
    {
        User user;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<PrintLogContext>();
            user = await AchievementTestData.CreateUserAsync(db);
            db.UserAchievements.Add(Row(user.Id));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var deletion = scope.ServiceProvider.GetRequiredService<IUserDeletionService>();
        var tracked = await context.Users.SingleAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);

        await deletion.DeleteAllDataForUser(tracked);

        Assert.Equal(0, await context.UserAchievements.CountAsync(a => a.UserId == user.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Print_DefaultsToUnknownSource()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await AchievementTestData.CreateUserAsync(db);
        var print = await AchievementTestData.AddPrintAsync(db, user);

        var source = await db.Prints.AsNoTracking().Where(p => p.Id == print.Id).Select(p => p.Source)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PrintSource.Unknown, source);
    }

    [Fact]
    public async Task SettingTypes_17to21_Seeded()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var names = await db.UserSettingTypes.Where(t => t.Id >= 17 && t.Id <= 21)
            .OrderBy(t => t.Id).Select(t => t.Name).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Achievements_ShowOnProfile", "Achievements_Celebrations", "Achievements_DismissedHint", "General_TimeZone", "Push_Achievement"],
            names);
    }
}
