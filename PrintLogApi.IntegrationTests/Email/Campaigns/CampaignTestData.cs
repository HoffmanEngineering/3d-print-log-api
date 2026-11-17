using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Email.Templates;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

/// <summary>Seeding for campaign tests. Timestamps are written after insert, because SaveChanges stamps CreatedDate with the real clock.</summary>
internal static class CampaignTestData
{
    public static readonly EmailFooterModel Footer = new(
        ReasonLine: "You're receiving this because of a test.",
        ManageUrl: "https://www.3dprintlog.test/email-preferences#m=MANAGE",
        UnsubscribeUrl: "https://www.3dprintlog.test/email-preferences#u=UNSUB",
        PostalAddress: "PO Box 0, Testville, USA",
        HomeUrl: "https://www.3dprintlog.test/");

    public static async Task SetZoneAsync(PrintLogContext db, long userId, string zone)
    {
        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            UserSettingTypeId = EvaluationContext.TimeZoneSettingTypeId,
            Value = zone,
            CreatedById = userId,
            UpdatedById = userId,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<Printer> PrinterAsync(PrintLogContext db, long userId, string name = "Prusa MK4", bool active = true)
    {
        var printer = new Printer { Name = name, Make = "Prusa", Model = "MK4", UserId = userId, IsActive = active };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return printer;
    }

    public static async Task<Print> PrintAsync(
        PrintLogContext db,
        long userId,
        long printerId,
        DateTimeOffset at,
        PrintSource source = PrintSource.Web,
        Print.PrintStatus status = Print.PrintStatus.Success,
        int printSeconds = 3600)
    {
        var print = new Print
        {
            Title = $"Print {Guid.NewGuid():N}",
            StartDate = at,
            Status = status,
            ViewStatus = Print.PrintViewStatus.Private,
            PrinterId = printerId,
            CreatedById = userId,
            UpdatedById = userId,
            PrintTimeInSeconds = printSeconds,
            Source = source,
        };
        db.Prints.Add(print);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var utc = at.UtcDateTime;
        await db.Prints.Where(p => p.Id == print.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreatedDate, utc), TestContext.Current.CancellationToken);
        return print;
    }
}
