using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Achievements.Triggers;

namespace PrintLogApi.Achievements;

public static class AchievementServiceCollectionExtensions
{
    /// <summary>Registers the achievement metrics, evaluator and save-time triggers.</summary>
    public static IServiceCollection AddAchievements(this IServiceCollection services)
    {
        foreach (var metric in CountMetrics.All().Concat(DateMetrics.All()))
        {
            services.AddSingleton<IAchievementMetric>(metric);
        }

        services.AddSingleton<ICatalogVersionProvider, CatalogVersionProvider>();
        services.AddScoped<IAchievementEvaluator, AchievementEvaluator>();
        services.AddScoped<IAchievementQueryService, AchievementQueryService>();
        services.AddSingleton<AchievementRarityService>();

        // Singletons: the interceptors are attached to every PrintLogContext, and the tracker's
        // per-context state must be the same instance across all of them.
        services.AddSingleton<AchievementTriggerTracker>();
        services.AddSingleton<AchievementSaveChangesInterceptor>();
        services.AddSingleton<AchievementTransactionInterceptor>();
        services.AddSingleton<IAchievementPassRunner, AchievementPassRunner>();

        return services;
    }
}
