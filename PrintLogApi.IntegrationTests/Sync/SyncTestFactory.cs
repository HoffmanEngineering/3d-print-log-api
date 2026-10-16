using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// The standard host with a <see cref="SettableTimeProvider"/> in place of the system clock, so a
/// connection can be made stale by moving the clock rather than by waiting 15 minutes. Tests
/// arrange their own users, so nothing they assert on is shared with a sibling.
/// </summary>
public sealed class SyncTestFactory : CustomWebApplicationFactory
{
    public static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public SettableTimeProvider Clock { get; } = new(Start);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>A fresh user, so a test's printers and spools are the only ones it sees.</summary>
    public async Task<User> CreateUserAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|sync-{Guid.NewGuid():N}",
            DisplayName = "Sync",
            ViewStatus = User.ProfileViewStatus.Private,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    public HttpRequestMessage Get(User user, string url, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return request;
    }

    /// <summary>Bumps the user's cache version, as every write does.</summary>
    public void Invalidate(User user) =>
        Services.GetRequiredService<PrintLogApi.Services.ICacheVersionService>().InvalidateUserCache(user.Id);
}
