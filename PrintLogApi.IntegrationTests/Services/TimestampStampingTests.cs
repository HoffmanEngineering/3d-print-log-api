using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// PrintLogContext stamps TimestampEntity audit dates on save, and an update must never change
/// CreatedDate (#256), however the modified entity reached the change tracker. The filament
/// controller tests cover the third way in: a child replaced by a new instance with the same key.
/// </summary>
public class TimestampStampingTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Update_OfALoadedEntity_KeepsCreatedDate_AndRestampsUpdatedDate()
    {
        var (id, created, _) = await InsertPrintAsync("Loaded timestamp print");

        // Backdate UpdatedDate so the restamp is visible however quickly the update follows.
        var backdated = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await context.Prints.Where(p => p.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedDate, backdated), TestContext.Current.CancellationToken);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = await context.Prints.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
            print.Title = "Edited";
            print.CreatedDate = default;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(created, print.CreatedDate);
        }

        var stored = await ReadPrintAsync(id);
        Assert.Equal("Edited", stored.Title);
        Assert.Equal(created, stored.CreatedDate);
        Assert.True(stored.UpdatedDate >= created, $"UpdatedDate was not restamped: {stored.UpdatedDate:O}");
    }

    [Fact]
    public async Task Update_OfADetachedEntity_DoesNotWriteItsCreatedDate()
    {
        var (id, created, _) = await InsertPrintAsync("Detached timestamp print");
        var detached = await ReadPrintAsync(id);
        detached.Title = "Edited while detached";
        detached.CreatedDate = default;

        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            context.Prints.Update(detached);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var stored = await ReadPrintAsync(id);
        Assert.Equal("Edited while detached", stored.Title);
        Assert.Equal(created, stored.CreatedDate);
    }

    private async Task<(long Id, DateTime Created, DateTime Updated)> InsertPrintAsync(string title)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = new Print
        {
            Title = title,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Private,
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId,
        };
        context.Prints.Add(print);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await ReadPrintAsync(print.Id);
        Assert.True(stored.CreatedDate.Year > 2000, $"Precondition: CreatedDate was {stored.CreatedDate:O}");
        return (print.Id, stored.CreatedDate, stored.UpdatedDate);
    }

    private async Task<Print> ReadPrintAsync(long id)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.Prints.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
    }
}
