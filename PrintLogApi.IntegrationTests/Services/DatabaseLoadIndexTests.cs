using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The indexes that keep the hottest background and polling queries off a full scan on the
/// 10-DTU production database (api#309), and the query shapes they depend on. Compiled through
/// the SQL Server provider without a database, like <c>FilamentSqlServerTranslationTests</c>:
/// the suite runs on SQLite, which would never notice a filtered index the queries cannot use.
/// </summary>
public class DatabaseLoadIndexTests
{
    private static PrintLogContext SqlServerContext() => new(new DbContextOptionsBuilder<PrintLogContext>()
        .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
        .Options);

    /// <summary>The design-time model: the runtime one drops annotations such as INCLUDE columns.</summary>
    private static IModel Model(PrintLogContext context) => context.GetService<IDesignTimeModel>().Model;

    private static IIndex Index(PrintLogContext context, Type entity, string name) =>
        Assert.Single(Model(context).FindEntityType(entity)!.GetIndexes(), i => i.GetDatabaseName() == name);

    private static string[] Columns(IIndex index) => index.Properties.Select(p => p.Name).ToArray();

    [Fact]
    public void SentTodayCount_HasAnIndexLeadingWithStatusThenSentAt()
    {
        using var context = SqlServerContext();

        var index = Index(context, typeof(EmailOutbox), "IX_EmailOutbox_Status_SentAt");
        Assert.Equal(["Status", "SentAt"], Columns(index));
    }

    [Fact]
    public void UnreadCounts_HaveAFilteredIndex_CoveringTheType()
    {
        using var context = SqlServerContext();

        var index = Index(context, typeof(Notification), "IX_Notifications_UserId_Type_Unread");
        Assert.Equal(["UserId", "Type"], Columns(index));
        Assert.Equal(NotificationService.UnreadFilter, index.GetFilter());
    }

    [Fact]
    public void TheOverlappingUnreadCountIndex_IsGone_AndTheListIndexStays()
    {
        using var context = SqlServerContext();
        var names = Model(context).FindEntityType(typeof(Notification))!.GetIndexes().Select(i => i.GetDatabaseName()).ToList();

        Assert.DoesNotContain("IX_Notifications_UserId_UnreadCount", names);
        Assert.Contains("IX_Notifications_UserId_IsRead_CreatedDate", names);
    }

    /// <summary>
    /// A filtered index serves a query only when the query's predicate is the filter as a literal.
    /// A parameterized <c>IsRead = @p</c> would compile a plan that has to work for either value,
    /// and SQL Server would never pick the index.
    /// </summary>
    [Fact]
    public void UnreadQuery_ComparesIsReadToALiteral_ThatMatchesTheIndexFilter()
    {
        using var context = SqlServerContext();

        var sql = NotificationService.Unread(context.Notifications, 42).ToQueryString();

        Assert.Contains("[n].[IsRead] = CAST(0 AS bit)", sql);
        Assert.Equal("[IsRead] = CAST(0 AS bit)", NotificationService.UnreadFilter);
    }

    [Fact]
    public void PublicPrintLists_HaveAnIndexLeadingWithViewStatus_ThatCoversTheProjection()
    {
        using var context = SqlServerContext();

        var index = Index(context, typeof(Print), "IX_Prints_ViewStatus");
        Assert.Equal(["ViewStatus"], Columns(index));
        Assert.Equal(["UpdatedDate"], index.GetIncludeProperties());
    }

    /// <summary>
    /// <c>PrinterPopularityJob.OwnerCounts</c> asks, per active printer, whether it has a print
    /// started since the cutoff. With only <c>(PrinterId)</c> to seek, SQL Server range-scanned the
    /// StartDate index once per printer, and every daily run hit the 30 s timeout. The same index
    /// serves <c>PrinterService.LinkPrintingRows</c> (newest print still printing on a printer).
    /// It replaces the plain foreign-key index, which it leads with.
    /// </summary>
    [Fact]
    public void PrinterActivity_HasAnIndexOnPrinterThenStartDate_CoveringStatus()
    {
        using var context = SqlServerContext();

        var index = Index(context, typeof(Print), "IX_Prints_PrinterId_StartDate");
        Assert.Equal(["PrinterId", "StartDate"], Columns(index));
        Assert.Equal(["Status"], index.GetIncludeProperties());

        var names = Model(context).FindEntityType(typeof(Print))!.GetIndexes().Select(i => i.GetDatabaseName()).ToList();
        Assert.DoesNotContain("IX_Prints_PrinterId", names);
    }

    /// <summary>
    /// A spool's remaining weight checks each usage row's print for its creator and printer
    /// (<c>SpoolOwnership.UsageRowCounts</c>), and the Spoolman use dates read each print's dates.
    /// Through the clustered index each of those is a read of a wide row (Notes is stored in
    /// row), thousands per filament list on S0. This narrow copy stays in memory.
    /// </summary>
    [Fact]
    public void PrintLookupsById_HaveANarrowCoveringIndex()
    {
        using var context = SqlServerContext();

        var index = Index(context, typeof(Print), "IX_Prints_Id_Covering");
        Assert.Equal(["Id"], Columns(index));
        Assert.Equal(["CreatedById", "PrinterId", "StartDate", "CreatedDate"], index.GetIncludeProperties());
    }
}
