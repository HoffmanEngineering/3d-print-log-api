namespace PrintLogApi.PrinterCatalog;

public static class PrinterCatalogServiceCollectionExtensions
{
    /// <summary>The printer catalog: its options, the in-memory index, and the startup seeder.</summary>
    public static IServiceCollection AddPrinterCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PrinterCatalogOptions>().Bind(configuration.GetSection(PrinterCatalogOptions.SectionName));
        services.AddSingleton<IPrinterCatalogIndex, PrinterCatalogIndex>();
        services.AddHostedService<PrinterCatalogSeeder>();
        return services;
    }
}
