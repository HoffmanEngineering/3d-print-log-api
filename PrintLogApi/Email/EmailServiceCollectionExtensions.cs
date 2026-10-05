using Microsoft.Extensions.Options;

namespace PrintLogApi.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>Registers the email platform. Everything is inert until <c>Email:Enabled</c> or <c>Email:DryRun</c> is set.</summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        services.AddSingleton<IEmailAddressHasher, EmailAddressHasher>();
        services.AddScoped<IEmailPreferenceService, EmailPreferenceService>();

        return services;
    }
}
