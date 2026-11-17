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
}
