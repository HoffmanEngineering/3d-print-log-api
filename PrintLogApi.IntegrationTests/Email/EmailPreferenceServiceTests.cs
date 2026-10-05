using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailPreferenceServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EmailPreferenceServiceTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task NewUser_EverythingOn_NoticeUnseen()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var prefs = scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>();
        var user = await EmailTestData.CreateUserAsync(db);

        var snapshot = await prefs.GetAsync(user.Id, TestContext.Current.CancellationToken);

        Assert.Equal(new EmailPreferenceSnapshot(true, true, true, true, null), snapshot);
    }

    [Fact]
    public async Task CampaignOptOut_OnlyAffectsThatCampaign()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var prefs = scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>();
        var user = await EmailTestData.CreateUserAsync(db);
        var ct = TestContext.Current.CancellationToken;

        await prefs.SetAsync(user.Id, EmailSettingTypes.MonthlyRecap, false, ct);

        Assert.False(await prefs.IsAllowedAsync(user.Id, EmailSettingTypes.MonthlyRecap, ct));
        Assert.True(await prefs.IsAllowedAsync(user.Id, EmailSettingTypes.PrinterSilent, ct));
    }

    [Fact]
    public async Task MasterOptOut_DisablesEveryCampaign()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var prefs = scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>();
        var user = await EmailTestData.CreateUserAsync(db);
        var ct = TestContext.Current.CancellationToken;

        await prefs.SetAsync(user.Id, EmailSettingTypes.All, false, ct);

        Assert.False(await prefs.IsAllowedAsync(user.Id, EmailSettingTypes.PrinterSilent, ct));
        Assert.False(await prefs.IsAllowedAsync(user.Id, EmailSettingTypes.Onboarding, ct));
    }

    [Fact]
    public async Task SetAsync_ReEnables()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var prefs = scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>();
        var user = await EmailTestData.CreateUserAsync(db);
        var ct = TestContext.Current.CancellationToken;

        await prefs.SetAsync(user.Id, EmailSettingTypes.MonthlyRecap, false, ct);
        await prefs.SetAsync(user.Id, EmailSettingTypes.MonthlyRecap, true, ct);

        Assert.True(await prefs.IsAllowedAsync(user.Id, EmailSettingTypes.MonthlyRecap, ct));
        Assert.Equal(1, await db.UserSettings.CountAsync(s => s.UserId == user.Id && s.UserSettingTypeId == EmailSettingTypes.MonthlyRecap, ct));
    }

    [Fact]
    public async Task UnrecognizedStoredValue_IsNotConsent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var prefs = scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>();
        var user = await EmailTestData.CreateUserAsync(db);
        var ct = TestContext.Current.CancellationToken;
        db.UserSettings.Add(new UserSetting { UserId = user.Id, UserSettingTypeId = EmailSettingTypes.Onboarding, Value = "garbage", CreatedById = user.Id, UpdatedById = user.Id });
        await db.SaveChangesAsync(ct);

        Assert.False(await prefs.IsAllowedAsync(user.Id, EmailSettingTypes.Onboarding, ct));
    }

    [Fact]
    public async Task NoticeSeenAt_IsReported()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var prefs = scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>();
        var user = await EmailTestData.CreateUserAsync(db);
        var ct = TestContext.Current.CancellationToken;
        var seen = new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero);
        db.UserSettings.Add(new UserSetting { UserId = user.Id, UserSettingTypeId = EmailSettingTypes.NoticeSeenAt, Value = seen.ToString("O"), CreatedById = user.Id, UpdatedById = user.Id });
        await db.SaveChangesAsync(ct);

        Assert.True(await prefs.HasSeenNoticeAsync(user.Id, ct));
        Assert.Equal(seen, (await prefs.GetAsync(user.Id, ct)).NoticeSeenAt);
    }

    [Fact]
    public async Task ConcurrentFirstWrites_LeaveOneRow()
    {
        long userId;
        using (var seed = _factory.Services.CreateScope())
        {
            userId = (await EmailTestData.CreateUserAsync(seed.ServiceProvider.GetRequiredService<PrintLogContext>())).Id;
        }

        var ct = TestContext.Current.CancellationToken;
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();
        var a = scopeA.ServiceProvider.GetRequiredService<IEmailPreferenceService>();
        var b = scopeB.ServiceProvider.GetRequiredService<IEmailPreferenceService>();

        await Task.WhenAll(
            a.SetAsync(userId, EmailSettingTypes.PrinterSilent, false, ct),
            b.SetAsync(userId, EmailSettingTypes.PrinterSilent, false, ct));

        using var check = _factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Equal(1, await db.UserSettings.CountAsync(s => s.UserId == userId && s.UserSettingTypeId == EmailSettingTypes.PrinterSilent, ct));
    }
}
