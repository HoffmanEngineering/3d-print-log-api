using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Two requests creating the same user setting at once. The requests really run in parallel, so
/// the host is on a <see cref="SqliteFileDatabase"/>: on the suite's shared in-memory connection,
/// one request's context starting while the other's query is mid-read fails with SQLite error 5
/// before the race under test is reached.
/// </summary>
public class UserSettingsInsertRaceTests(UserSettingsInsertRaceTests.Factory factory)
    : IClassFixture<UserSettingsInsertRaceTests.Factory>
{
    public sealed class Factory : CustomWebApplicationFactory
    {
        private readonly SqliteFileDatabase _database = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => _database.Register(services));
        }

        protected override void PrepareDatabase(PrintLogContext db)
        {
            base.PrepareDatabase(db);
            SqliteFileDatabase.UseWriteAheadLogging(db);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _database.Dispose();
            }
        }
    }

    private readonly HttpClient _httpClient = factory.CreateClient();

    private int CreateSettingType()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var settingType = new UserSettingType
        {
            Name = $"RaceType-{Guid.NewGuid():N}",
            Description = "Test setting type for the insert race",
        };
        db.UserSettingTypes.Add(settingType);
        db.SaveChanges();
        return settingType.Id;
    }

    [Fact]
    public async Task CreateUserSetting_LosingAnInsertRace_ReturnsConflictNotServerError()
    {
        var settingTypeId = CreateSettingType();

        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Users/me/user-settings");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            var payload = JsonSerializer.Serialize(new
            {
                userSettingTypeId = settingTypeId,
                value = "true"
            });
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }

        // Fired together so both requests can pass the "does it already exist?" pre-check
        // before either saves. IX_UserSettings_UserId_UserSettingTypeId then rejects the
        // second write; before this was handled that surfaced as an unhandled 500.
        var responses = await Task.WhenAll(
            _httpClient.SendAsync(Request(), TestContext.Current.CancellationToken),
            _httpClient.SendAsync(Request(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);

        // Exactly one row exists regardless of which side won.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var rows = await db.UserSettings
            .Where(u => u.UserId == IntegrationTestSeeder.TestUserId
                        && u.UserSettingTypeId == settingTypeId)
            .CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, rows);
    }
}
