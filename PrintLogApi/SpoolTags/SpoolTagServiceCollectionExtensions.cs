using PrintLogApi.SpoolTags.Catalog;
using PrintLogApi.SpoolTags.Decoding;

namespace PrintLogApi.SpoolTags;

public static class SpoolTagServiceCollectionExtensions
{
    /// <summary>Registers spool tag reading: the decoders, the catalog import and its maintenance worker.</summary>
    public static IServiceCollection AddSpoolTags(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SpoolTagOptions>().Bind(configuration.GetSection(SpoolTagOptions.SectionName));

        services.AddHttpClient<SpoolmanDbClient>(SpoolmanDbClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddScoped<FilamentCatalogImporter>();
        services.AddScoped<Matching.FilamentCatalogMatcher>();
        services.AddScoped<Matching.TagSuggestionRanker>();
        services.AddScoped<TagReadService>();
        services.AddHostedService<SpoolTagMaintenanceService>();

        services.AddSingleton<ITagDecoder, SnapmakerDecoder>();
        services.AddSingleton<ITagDecoder, AnycubicAceDecoder>();
        services.AddSingleton<ITagDecoder, NdefOpenSpoolDecoder>();
        services.AddSingleton<TagDecoderRegistry>();

        return services;
    }
}
