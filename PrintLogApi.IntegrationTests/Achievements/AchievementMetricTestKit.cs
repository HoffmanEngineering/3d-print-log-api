using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

/// <summary>Counts the commands a context executes, for query-budget assertions.</summary>
public sealed class CommandCounter : DbCommandInterceptor
{
    public List<string> Commands { get; } = [];

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Commands.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }
}

/// <summary>Builds evaluation contexts and seeds the less common entities metrics read.</summary>
public static class AchievementMetricTestKit
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A context over the factory's shared connection with a command counter attached. Built by
    /// hand, as GroupedFeedQueryShapeTests does, so the app's own registration is untouched.
    /// </summary>
    public static (PrintLogContext Db, CommandCounter Counter) CountingContext(IServiceProvider sp)
    {
        var connection = sp.GetRequiredService<PrintLogContext>().Database.GetDbConnection();
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;
        return (new PrintLogContext(options), counter);
    }

    public static EvaluationContext Context(IServiceProvider sp, PrintLogContext db, long userId, TimeZoneInfo? zone = null, DateTime? nowUtc = null) =>
        new(userId, db, zone ?? TimeZoneInfo.Utc, nowUtc ?? DateTime.UtcNow, sp.GetServices<IAchievementMetric>());

    public static async Task<Filament> AddFilamentAsync(PrintLogContext db, User user, string? materialType = "PLA")
    {
        var filament = new Filament
        {
            Id = Guid.NewGuid(),
            DisplayName = $"Spool {Guid.NewGuid():N}"[..20],
            MaterialType = materialType,
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            CreatedById = user.Id,
            UpdatedById = user.Id,
        };
        db.Filaments.Add(filament);
        await db.SaveChangesAsync(Ct);
        return filament;
    }

    public static async Task AddUsageAsync(PrintLogContext db, Print print, Guid? filamentId, int? amountMg = 1000, int? estimatedMg = null)
    {
        db.PrintFilament.Add(new PrintFilament
        {
            PrintId = print.Id,
            FilamentId = filamentId,
            AmountMg = amountMg,
            EstimatedAmountMg = estimatedMg,
            Source = PrintFilament.SourceMeasurement.Weight,
            EstimatedSource = PrintFilament.SourceMeasurement.Weight,
        });
        await db.SaveChangesAsync(Ct);
    }

    public static async Task AddCommentAsync(PrintLogContext db, Print print, long commenterId)
    {
        var comment = new Comment { Body = "Nice", CreatedById = commenterId, UpdatedById = commenterId };
        db.PrintComments.Add(new PrintComment
        {
            Print = print,
            Comment = comment,
            CreatedById = commenterId,
            UpdatedById = commenterId,
        });
        await db.SaveChangesAsync(Ct);
    }

    public static async Task AddImageAsync(PrintLogContext db, Print print, long userId)
    {
        db.PrintImages.Add(new PrintImage
        {
            PrintId = print.Id,
            File = new Models.File
            {
                Id = Guid.NewGuid(),
                Path = $"printimages/{Guid.NewGuid():N}.jpg",
                Size = 10,
                CreatedById = userId,
                UpdatedById = userId,
            },
            IsDefault = true,
            CreatedById = userId,
            UpdatedById = userId,
        });
        await db.SaveChangesAsync(Ct);
    }
}
