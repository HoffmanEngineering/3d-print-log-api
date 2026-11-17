using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

internal static class EmailTestData
{
    public static async Task<User> CreateUserAsync(
        PrintLogContext db,
        string? email = null,
        bool verified = true,
        DateTimeOffset? createdDate = null)
    {
        var user = new User
        {
            OAuthUserId = $"auth0|email-{Guid.NewGuid():N}",
            ViewStatus = User.ProfileViewStatus.Public,
            Email = email ?? $"maker-{Guid.NewGuid():N}@example.com",
            EmailVerified = verified,
            CreatedDate = createdDate,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    public static EmailOutbox OutboxRow(
        long userId,
        string campaign = "monthly-recap",
        string periodKey = "2026-11",
        DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return new EmailOutbox
        {
            UserId = userId,
            Campaign = campaign,
            PeriodKey = periodKey,
            Status = EmailOutboxStatus.Pending,
            SendAfter = at,
            NextAttemptAt = at,
            ExpiresAt = at.AddDays(2),
            CreatedAt = at,
        };
    }
}
