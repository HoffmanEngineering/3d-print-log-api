using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.Models;

namespace PrintLogApi.IntegrationTests.Analytics;

/// <summary>
/// A fixture whose every date is expressed relative to <see cref="Pinned"/> rather than to the
/// real clock, paired with a <see cref="SettableTimeProvider"/> registered over
/// <c>TimeProvider.System</c>.
///
/// This is what the other analytics fixtures cannot do. They date their prints from
/// <c>DateTimeOffset.UtcNow</c>, so "the day a streak lapses" or "the day a print ages out of
/// the 90-day burn window" is not a case they can construct — the boundary is always exactly
/// as far away as the seeding ran, and moving to it means waiting a day. Here the boundary is
/// crossed by moving the clock, and the assertions are exact numbers rather than inequalities.
///
/// Deliberately its OWN factory rather than more rows on <c>McpTestData</c>: every totals
/// assertion in the analytics suite is written against that fixture's exact contents, so an
/// extra user's prints would be invisible to the scoped queries but not to a reader trying to
/// work out which numbers are load-bearing.
/// </summary>
public sealed class PinnedClockDataFactory : CustomWebApplicationFactory
{
    /// <summary>
    /// The instant the clock starts at. A midday UTC time on a fixed past date: midday so a
    /// "local today" in any plausible test timezone is the same calendar day as the UTC one,
    /// and past so nothing here depends on when the suite runs.
    /// </summary>
    public static readonly DateTimeOffset Pinned = new(2025, 6, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Spool size, in mg. 200 g, chosen so the runway figures below stay under the 365-day cap.</summary>
    public const long SpoolInitialMg = 200_000;

    /// <summary>
    /// Grams on the print that sits one day INSIDE the 90-day burn window at
    /// <see cref="Pinned"/> and two days outside it after the clock advances by two.
    /// </summary>
    public const int AgingPrintMg = 90_000;

    /// <summary>Grams on each of the three consecutive-day prints that form the streak.</summary>
    public const int StreakPrintMg = 20_000;

    public const string PinnedUserOAuthId = "auth0|analytics-pinned-clock-user";

    /// <summary>
    /// The clock every service in this host reads. Shared across the class's tests, so each
    /// one SETS the instant it wants rather than advancing from wherever the last test left
    /// it — see <see cref="SettableTimeProvider"/> for why that rules out FakeTimeProvider.
    /// </summary>
    public SettableTimeProvider Clock { get; } = new(Pinned);

    public long UserId { get; private set; }
    public Guid SpoolId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            // Replace, not Add: Startup registers TimeProvider.System, and a second
            // registration of the same service type would leave the last one to win by
            // ordering rather than by intent.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        Seed(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
        return host;
    }

    private void Seed(PrintLogContext context)
    {
        var user = new User
        {
            OAuthUserId = PinnedUserOAuthId,
            ViewStatus = User.ProfileViewStatus.Public,
        };
        context.Users.Add(user);
        context.SaveChanges();
        UserId = user.Id;

        var printer = new Printer
        {
            Name = "Pinned Clock Printer",
            Make = "Test",
            Model = "Fixed",
            UserId = UserId,
            IsActive = true,
        };
        context.Printers.Add(printer);
        context.SaveChanges();

        SpoolId = Guid.NewGuid();
        context.Filaments.Add(new Filament
        {
            Id = SpoolId,
            Brand = "Pinned",
            ColorHex = "00FF00",
            ColorName = "Green",
            DisplayName = "Pinned Clock PLA",
            MaterialType = "PLA",
            MaterialCategoryNickname = "filament",
            MaterialDensityGramPerCubicCm = 1.24,
            DiameterMm = 1.75,
            IsActive = true,
            InitialNominalWeightMg = SpoolInitialMg,
            Source = Filament.SourceMeasurement.Weight,
            CreatedById = UserId,
            CreatedDate = Pinned.UtcDateTime,
            UpdatedById = UserId,
            UpdatedDate = Pinned.UtcDateTime,
        });
        context.SaveChanges();

        // Day -89: inside the 90-day burn window at Pinned, outside it at Pinned + 2 days.
        // Deliberately NOT part of the streak — an 87-day gap separates it from the run below,
        // so it cannot extend one.
        AddPrint(context, printer.Id, Pinned.AddDays(-89), AgingPrintMg);

        // Days -2, -1 and 0: a three-day run ending on the pinned "today". At Pinned the
        // streak is current; at Pinned + 1 it survives on the grace day (Streaks counts
        // yesterday, because today may simply not have happened yet); at Pinned + 2 it lapses.
        AddPrint(context, printer.Id, Pinned.AddDays(-2), StreakPrintMg);
        AddPrint(context, printer.Id, Pinned.AddDays(-1), StreakPrintMg);
        AddPrint(context, printer.Id, Pinned.AddHours(-2), StreakPrintMg);

        context.SaveChanges();
    }

    private void AddPrint(PrintLogContext context, long printerId, DateTimeOffset start, int usedMg)
    {
        context.Prints.Add(new Print
        {
            Title = $"Pinned print {start:yyyy-MM-dd}",
            StartDate = start,
            Status = Print.PrintStatus.Success,
            ViewStatus = Print.PrintViewStatus.Private,
            PrinterId = printerId,
            CreatedById = UserId,
            CreatedDate = start.UtcDateTime,
            UpdatedById = UserId,
            UpdatedDate = start.UtcDateTime,
            PrintTimeInSeconds = 3600,
            FilamentUsage = new List<PrintFilament>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    FilamentId = SpoolId,
                    AmountMg = usedMg,
                },
            },
        });
    }
}
