using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.SpoolTags;
using PrintLogApi.SpoolTags.Catalog;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>
/// A private service provider and SQLite database for the catalog tests, so they do not share rows
/// with any other test. The SpoolmanDB client talks to <see cref="Stub"/>, never the network.
/// </summary>
public sealed class CatalogTestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public SpoolmanDbStub Stub { get; } = new();

    public CatalogTestHost(bool importEnabled = true, long? maxCatalogBytes = null, TimeSpan? importTimeout = null)
    {
        _connection.Open();
        SqliteSearchCollation.Register(_connection);

        var settings = new Dictionary<string, string?>
        {
            ["SpoolTags:CatalogImportEnabled"] = importEnabled.ToString(),
        };
        if (maxCatalogBytes != null)
        {
            settings["SpoolTags:MaxCatalogBytes"] = maxCatalogBytes.Value.ToString();
        }
        if (importTimeout != null)
        {
            settings["SpoolTags:ImportTimeout"] = importTimeout.Value.ToString("c");
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<PrintLogContext>(o => o.UseSqlite(_connection));
        services.AddSpoolTags(configuration);
        services.AddHttpClient(SpoolmanDbClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => Stub.CreateHandler());
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<PrintLogContext>().Database.EnsureCreated();
        FilamentCatalogImporter.ResetETag();
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public SpoolTagMaintenanceService Maintenance() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SpoolTagOptions>>(),
        TimeProvider.System,
        _provider.GetRequiredService<ILogger<SpoolTagMaintenanceService>>());

    public async ValueTask DisposeAsync()
    {
        FilamentCatalogImporter.ResetETag();
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
