using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using PrintLogApi.PrinterCatalog;
using PrintLogApi.PrinterCatalog.Generation;
using Xunit;

namespace PrintLogApi.IntegrationTests.PrinterCatalog;

/// <summary>
/// A private service provider and SQLite database for the seeder tests, so they share no rows with
/// any other test and can start from an empty catalog table.
/// </summary>
public sealed class PrinterCatalogTestHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public SettableTimeProvider Time { get; } = new(Start);

    /// <summary>The host lifetime the seeder waits on; <see cref="FakeLifetime.Start"/> fires ApplicationStarted.</summary>
    public FakeLifetime Lifetime { get; } = new();

    public PrinterCatalogTestHost()
    {
        _connection.Open();
        SqliteSearchCollation.Register(_connection);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddDbContext<PrintLogContext>(o => o.UseSqlite(_connection));
        services.AddPrinterCatalog(new ConfigurationBuilder().Build());
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<PrintLogContext>().Database.EnsureCreated();
    }

    public IPrinterCatalogIndex Index => _provider.GetRequiredService<IPrinterCatalogIndex>();

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    /// <summary>The hosted service, reading <paramref name="load"/> (the embedded file by default).</summary>
    public PrinterCatalogSeeder Seeder(Func<CatalogFile>? load = null) => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        Index,
        Time,
        Lifetime,
        _provider.GetRequiredService<ILogger<PrinterCatalogSeeder>>())
    {
        LoadCatalog = load ?? PrinterCatalogFileLoader.LoadEmbedded,
    };

    /// <summary>Seeds through a fresh context, as the hosted service does.</summary>
    public async Task<SeedResult> SeedAsync(CatalogFile file)
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await PrinterCatalogSeeder.SeedAsync(db, file, Time.GetUtcNow(), TestContext.Current.CancellationToken);
    }

    public async Task<Dictionary<string, PrinterModel>> RowsAsync()
    {
        await using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>()
            .PrinterModels.AsNoTracking().ToDictionaryAsync(m => m.Id, TestContext.Current.CancellationToken);
    }

    public async Task<string?> MarkerAsync()
    {
        await using var scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>()
            .PrinterCatalogVersions.Select(v => v.Version).SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Adds a user with one printer per catalog id, and returns the printers' ids in order.</summary>
    public async Task<long[]> AddPrintersAsync(params string[] catalogModelIds)
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User { OAuthUserId = $"catalog-{Guid.NewGuid():N}" };
        var printers = catalogModelIds.Select(id => new Printer { User = user, Name = id, IsActive = true, CatalogModelId = id }).ToList();
        db.Printers.AddRange(printers);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return printers.Select(p => p.Id).ToArray();
    }

    public async Task<string?[]> PrinterLinksAsync(params long[] printerIds)
    {
        await using var scope = CreateScope();
        var links = await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Printers
            .Where(p => printerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.CatalogModelId, TestContext.Current.CancellationToken);
        return printerIds.Select(id => links[id]).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        Lifetime.Dispose();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>An <see cref="IHostApplicationLifetime"/> whose events fire only when a test says so.</summary>
public sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => _stopped.Token;

    public void Start() => _started.Cancel();

    public void StopApplication() => _stopping.Cancel();

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}
