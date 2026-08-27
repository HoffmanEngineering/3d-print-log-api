using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;

namespace PrintLogApi.IntegrationTests.Support;

/// <summary>
/// Seeding for the project-date tests. Every helper takes a caller-supplied unique name so
/// tests stay isolated on the shared test database — assertions must filter by the returned
/// id or the supplied name, never by position or by <c>Single()</c> over an unfiltered set.
/// </summary>
public static class ProjectDateSeedHelpers
{
    /// <summary>
    /// A project with two prints: an earlier 4-day print and a later 1-hour print.
    /// Derived start is 2026-03-02; derived finish is 2026-03-06, because the LONG print
    /// finishes last. A max-of-start-dates implementation would wrongly say 2026-03-05.
    /// </summary>
    public static Task<Guid> SeedProjectWithPrintsAsync(
        CustomWebApplicationFactory factory, long userId, string uniqueName) =>
        SeedAsync(factory, userId, uniqueName, prints:
        [
            (Start: "2026-03-02T10:00:00Z", Seconds: 4 * 24 * 3600),
            (Start: "2026-03-05T10:00:00Z", Seconds: 3600),
        ]);

    /// <summary>A project with no prints at all: both dates fall back to the creation date.</summary>
    public static Task<Guid> SeedProjectWithNoPrintsAsync(
        CustomWebApplicationFactory factory, long userId, string uniqueName) =>
        SeedAsync(factory, userId, uniqueName, prints: []);

    /// <summary>
    /// A project whose prints all have a null StartDate. Distinct from "no prints": the
    /// collection is non-empty, so a naive Min()/Max() would throw or return default.
    /// </summary>
    public static Task<Guid> SeedProjectWithUndatedPrintsAsync(
        CustomWebApplicationFactory factory, long userId, string uniqueName) =>
        SeedAsync(factory, userId, uniqueName, prints:
        [
            (Start: null, Seconds: 3600),
            (Start: null, Seconds: 60),
        ]);

    /// <summary>
    /// N projects pinned to the same StartDateOverride, for proving the feed's sort is
    /// stable when the sort key ties. Returns their ids in creation order.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> SeedProjectsPinnedToSameDayAsync(
        CustomWebApplicationFactory factory, long userId, string namePrefix, int count, DateOnly day)
    {
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(await SeedAsync(
                factory, userId, $"{namePrefix}-{i}", prints: [], startOverride: day));
        }

        return ids;
    }

    /// <summary>
    /// Seeds one project plus its prints. <paramref name="prints"/> carries a start instant
    /// (null for an undated print) and a duration in seconds.
    /// </summary>
    public static async Task<Guid> SeedAsync(
        CustomWebApplicationFactory factory,
        long userId,
        string uniqueName,
        IReadOnlyList<(string? Start, int Seconds)> prints,
        DateOnly? startOverride = null,
        DateOnly? finishOverride = null,
        Project.ProjectViewStatus viewStatus = Project.ProjectViewStatus.Private)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var now = DateTime.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = uniqueName,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = viewStatus,
            StartDateOverride = startOverride,
            FinishDateOverride = finishOverride,
            CreatedById = userId,
            CreatedDate = now,
            UpdatedById = userId,
            UpdatedDate = now,
        };
        context.Projects.Add(project);

        foreach (var (start, seconds) in prints)
        {
            context.Prints.Add(new Print
            {
                Title = uniqueName,
                PrinterId = IntegrationTestSeeder.TestPrinterId,
                ProjectId = project.Id,
                StartDate = start is null ? null : DateTimeOffset.Parse(start),
                PrintTimeInSeconds = seconds,
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Private,
                CreatedById = userId,
                CreatedDate = now,
                UpdatedById = userId,
                UpdatedDate = now,
            });
        }

        await context.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>
    /// A project whose two prints carry the SAME wall-clock text but different offsets.
    /// 2026-03-02T01:00+09:00 is 2026-03-01T16:00Z — genuinely earlier than 2026-03-02T00:00Z,
    /// even though its text form sorts later. A SQL MIN over DateTimeOffset on SQLite, which
    /// stores the offset suffix and compares lexicographically, picks the wrong one.
    /// </summary>
    public static Task<Guid> SeedProjectWithMixedOffsetPrintsAsync(
        CustomWebApplicationFactory factory, long userId, string uniqueName) =>
        SeedAsync(factory, userId, uniqueName, prints:
        [
            (Start: "2026-03-02T00:00:00Z", Seconds: 3600),
            (Start: "2026-03-02T01:00:00+09:00", Seconds: 3600),
        ]);

    /// <summary>A print with no project, for proving cross-type feed ordering.</summary>
    public static async Task<long> SeedStandalonePrintAsync(
        CustomWebApplicationFactory factory, long userId, string start, string title)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var now = DateTime.UtcNow;
        var print = new Print
        {
            Title = title,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            ProjectId = null,
            StartDate = DateTimeOffset.Parse(start),
            PrintTimeInSeconds = 3600,
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Private,
            CreatedById = userId,
            CreatedDate = now,
            UpdatedById = userId,
            UpdatedDate = now,
        };
        context.Prints.Add(print);
        await context.SaveChangesAsync();
        return print.Id;
    }

    /// <summary>Pins a seeded project's start date, bypassing the API.</summary>
    public static async Task SetStartOverrideAsync(
        CustomWebApplicationFactory factory, Guid projectId, DateOnly day)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var project = await context.Projects.FirstAsync(p => p.Id == projectId);
        project.StartDateOverride = day;
        await context.SaveChangesAsync();
    }
}
