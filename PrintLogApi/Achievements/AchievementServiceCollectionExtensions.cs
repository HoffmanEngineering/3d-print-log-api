using PrintLogApi.Achievements.Metrics;

namespace PrintLogApi.Achievements;

public static class AchievementServiceCollectionExtensions
{
    /// <summary>Registers the achievement metrics, evaluator and save-time triggers.</summary>
    public static IServiceCollection AddAchievements(this IServiceCollection services)
    {
        foreach (var metric in CountMetrics.All())
        {
            services.AddSingleton<IAchievementMetric>(metric);
        }

        return services;
    }
}
