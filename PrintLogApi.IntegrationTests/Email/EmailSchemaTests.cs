using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailSchemaTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EmailSchemaTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task UniqueOutboxKey_RejectsSecondRowForSameUserCampaignPeriod()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);

        db.EmailOutbox.Add(EmailTestData.OutboxRow(user.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.EmailOutbox.Add(EmailTestData.OutboxRow(user.Id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuppressionHash_IsUnique()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        // 64 hex characters, unique per run so the shared database never collides across tests.
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        db.EmailSuppressions.Add(new EmailSuppression { EmailHash = hash, Reason = EmailSuppressionReason.Bounce, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.EmailSuppressions.Add(new EmailSuppression { EmailHash = hash, Reason = EmailSuppressionReason.Complaint, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmailSettingTypes_AreSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var types = await db.UserSettingTypes
            .Where(t => t.Id >= EmailSettingTypes.All && t.Id <= EmailSettingTypes.NoticeSeenAt)
            .OrderBy(t => t.Id)
            .Select(t => t.Id + ":" + t.Name)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["22:Email_All", "23:Email_Onboarding", "24:Email_MonthlyRecap", "25:Email_PrinterSilent", "26:Email_NoticeSeenAt"],
            types);
    }

    [Fact]
    public async Task DeletingUser_CascadesOutbox()
    {
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await EmailTestData.CreateUserAsync(db);
            userId = user.Id;
            db.EmailOutbox.Add(EmailTestData.OutboxRow(userId));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await db.EmailOutbox.CountAsync(o => o.UserId == userId, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UserEmailColumns_RoundTrip()
    {
        var created = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        long userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await EmailTestData.CreateUserAsync(db, email: "round@trip.example", verified: true, createdDate: created);
            user.EmailUpdatedAt = created;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            Assert.Equal("round@trip.example", user.Email);
            Assert.True(user.EmailVerified);
            Assert.Equal(created, user.CreatedDate);
            Assert.Equal(created, user.EmailUpdatedAt);
        }
    }
}
