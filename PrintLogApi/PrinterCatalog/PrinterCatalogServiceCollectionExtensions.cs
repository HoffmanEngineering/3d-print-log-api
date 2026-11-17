namespace PrintLogApi.PrinterCatalog;

public static class PrinterCatalogServiceCollectionExtensions
{
    /// <summary>The printer catalog: its options, the in-memory index, the startup seeder, the backfill it runs once the index is built, and the daily popularity job.</summary>
    public static IServiceCollection AddPrinterCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PrinterCatalogOptions>().Bind(configuration.GetSection(PrinterCatalogOptions.SectionName));
        services.AddSingleton<IPrinterCatalogIndex, PrinterCatalogIndex>();
        services.AddSingleton<PrinterCatalogBackfill>();
        services.AddSingleton<PrinterPopularityJob>();
        services.AddHostedService<PrinterPopularityJob>();
        services.AddHostedService<PrinterCatalogSeeder>();
        return services;
    }
}
