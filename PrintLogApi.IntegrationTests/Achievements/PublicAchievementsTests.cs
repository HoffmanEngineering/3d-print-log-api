using System.Net;
using PrintLogApi.Achievements.Triggers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Achievements;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class PublicAchievementsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PublicAchievementsTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Viewer { Anonymous, OtherUser, Owner }

    public static TheoryData<Viewer, User.ProfileViewStatus, bool> Matrix()
    {
        var data = new TheoryData<Viewer, User.ProfileViewStatus, bool>();
        foreach (var viewer in Enum.GetValues<Viewer>())
            foreach (var status in Enum.GetValues<User.ProfileViewStatus>())
                foreach (var hidden in new[] { false, true })
                {
                    data.Add(viewer, status, hidden);
                }
        return data;
    }

    private async Task<(User Target, User Other)> SeedAsync(User.ProfileViewStatus status, bool hideOnProfile)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        scope.ServiceProvider.GetRequiredService<AchievementTriggerTracker>().Suppress(db);
        var target = await AchievementTestData.CreateUserAsync(db);
        var other = await AchievementTestData.CreateUserAsync(db);
        target.ViewStatus = status;
        db.UserAchievements.Add(new UserAchievement { UserId = target.Id, AchievementKey = "first-print", Tier = 1, UnlockedAt = DateTime.UtcNow });
        if (hideOnProfile)
        {
            db.UserSettings.Add(new UserSetting { UserId = target.Id, UserSettingTypeId = 17, Value = "false", CreatedById = target.Id, UpdatedById = target.Id });
        }
        await db.SaveChangesAsync(Ct);
        return (target, other);
    }

    private async Task<(HttpStatusCode Status, PublicAchievementsDto? Body)> GetAsync(long targetId, User? viewer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/users/{targetId}/achievements");
        if (viewer is null)
        {
            // What the web app sends on a public route.
            request.Headers.Add("allow-anonymous-request", "true");
        }
        else
        {
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, viewer.OAuthUserId);
        }

        var response = await _factory.CreateClient().SendAsync(request, Ct);
        var body = response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync<PublicAchievementsDto>(Ct)
            : null;
        return (response.StatusCode, body);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task PublicAchievements_Matrix(Viewer viewer, User.ProfileViewStatus status, bool hideOnProfile)
    {
        var (target, other) = await SeedAsync(status, hideOnProfile);
        var as_ = viewer switch { Viewer.Owner => target, Viewer.OtherUser => other, _ => null };

        var (code, body) = await GetAsync(target.Id, as_);

        // Never a 403 or 404: a refusal would bounce a logged-out visitor off the public route.
        Assert.Equal(HttpStatusCode.OK, code);
        var visible = viewer == Viewer.Owner
            || (status is User.ProfileViewStatus.Public or User.ProfileViewStatus.Unlisted && !hideOnProfile);
        Assert.Equal(visible ? 1 : 0, body!.EarnedTierCount);
        Assert.Equal(visible ? 1 : 0, body.Families.Count);
    }

    [Fact]
    public async Task UnknownUser_ReturnsEmpty200()
    {
        var (code, body) = await GetAsync(long.MaxValue, null);

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(0, body!.EarnedTierCount);
        Assert.Empty(body.Featured);
    }

    [Fact]
    public async Task Featured_TopFourByTierThenRarityThenRecency_AndRevealsEarnedHidden()
    {
        User target;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            scope.ServiceProvider.GetRequiredService<AchievementTriggerTracker>().Suppress(db);
            target = await AchievementTestData.CreateUserAsync(db);
            var t0 = DateTime.UtcNow.AddDays(-10);
            void Hold(string key, int tier, int day) =>
                db.UserAchievements.Add(new UserAchievement { UserId = target.Id, AchievementKey = key, Tier = tier, UnlockedAt = t0.AddDays(day) });
            Hold("prints-logged", 1, 0); Hold("prints-logged", 2, 1); Hold("prints-logged", 3, 2);
            Hold("print-hours", 1, 0); Hold("print-hours", 2, 3);
            Hold("first-print", 1, 0);
            Hold("first-printer", 1, 4);
            Hold("spaghetti", 1, 5);
            Hold("projects", 1, 6);
            await db.SaveChangesAsync(Ct);
        }

        var (_, body) = await GetAsync(target.Id, null);

        Assert.Equal(9, body!.EarnedTierCount);
        Assert.Equal(4, body.Featured.Count);
        Assert.Equal(["prints-logged", "print-hours"], body.Featured.Take(2).Select(f => f.Key));
        Assert.Equal(3, body.Featured[0].HighestTier);
        Assert.Contains(body.RevealedHidden, f => f.Key == "spaghetti" && f.Title == "Spaghetti Monster");
        Assert.DoesNotContain(body.RevealedHidden, f => f.Key == "night-owl");
    }
}
