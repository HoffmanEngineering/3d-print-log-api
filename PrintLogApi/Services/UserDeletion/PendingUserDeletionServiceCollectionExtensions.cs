using Microsoft.Extensions.Options;

namespace PrintLogApi.Services.UserDeletion;

public static class PendingUserDeletionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the job that deletes accounts once their deactivation grace period is over. An
    /// invalid <c>PendingUserDeletion:RunInterval</c> stops the host at startup.
    /// </summary>
    public static IServiceCollection AddPendingUserDeletion(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<PendingUserDeletionOptions>()
            .Bind(configuration.GetSection(PendingUserDeletionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PendingUserDeletionOptions>, PendingUserDeletionOptionsValidator>();

        if (environment.IsEnvironment("E2ETesting"))
        {
            // The E2E database is recreated on every start and its users are fixtures, some of
            // them deactivated on purpose. Off here whatever the untracked appsettings file says,
            // so the job can never delete them, or call Auth0 for them.
            services.PostConfigure<PendingUserDeletionOptions>(o => o.Enabled = false);
        }

        services.AddHostedService<PendingUserDeletionJob>();
        return services;
    }
}
