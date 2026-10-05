using Microsoft.Extensions.Options;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Events;
using PrintLogApi.Email.Outbox;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.Email.Transport;

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
        services.AddScoped<IUserEmailSyncService, UserEmailSyncService>();
        services.AddSingleton<IEmailTokenService, EmailTokenService>();
        services.AddSingleton<IEmailTransport, SesEmailTransport>();
        services.AddSingleton<EmailLinkBuilder>();
        services.AddSingleton<EmailAssets>();
        services.AddSingleton<IEmailFooterFactory, EmailFooterFactory>();
        services.AddSingleton<IEmailTemplateRenderer, EmailTemplateRenderer>();

        // Campaigns are scoped: each holds the request's PrintLogContext.
        services.AddScoped<IEmailCampaign, OnboardingCampaign>();
        services.AddScoped<IEmailCampaign, MonthlyRecapCampaign>();
        services.AddScoped<IEmailCampaign, PrinterSilentCampaign>();

        services.AddScoped<CampaignEvaluator>();
        services.AddHostedService<CampaignEvaluatorService>();
        services.AddScoped<EmailDispatcher>();
        services.AddHostedService<EmailDispatcherService>();

        services.AddScoped<IEmailSuppressionService, EmailSuppressionService>();
        services.AddScoped<SesEventProcessor>();
        services.AddSingleton<ISnsMessageVerifier, SnsMessageVerifier>();
        services.AddHttpClient(EmailEventsConstants.SnsHttpClient, c => c.Timeout = TimeSpan.FromSeconds(10));

        return services;
    }
}
