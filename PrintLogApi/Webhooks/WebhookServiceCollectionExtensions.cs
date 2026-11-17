using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers outbound webhooks (#235). Without <c>Webhooks:SecretEncryptionKeys</c> everything
    /// stays registered but inert: the endpoints answer 503 and queued events are skipped.
    /// </summary>
    public static IServiceCollection AddWebhooks(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<WebhookOptions>, WebhookOptionsValidator>();

        var requestedPrivate = configuration.GetValue<bool>($"{WebhookOptions.SectionName}:{nameof(WebhookOptions.AllowPrivateAddresses)}");
        var allowPrivate = AllowPrivateAddresses(requestedPrivate, environment);
        if (requestedPrivate && !allowPrivate)
        {
            Console.Error.WriteLine(
                $"Webhooks:AllowPrivateAddresses is ignored in {environment.EnvironmentName}; it is honored only in Development.");
        }

        services.AddSingleton<IWebhookHostResolver, DnsWebhookHostResolver>();
        services.AddSingleton(sp => new WebhookAddressGuard(sp.GetRequiredService<IWebhookHostResolver>(), allowPrivate));
        services.AddSingleton<WebhookSecretProtector>();
        services.AddSingleton<WebhookEventQueue>();
        services.AddSingleton<IWebhookDispatcher, WebhookDispatcher>();
        services.AddSingleton<WebhookSender>();
        services.AddScoped<WebhookDeliveryProcessor>();
        services.AddScoped<IWebhookSubscriptionService, WebhookSubscriptionService>();
        services.AddHostedService<WebhookDeliveryService>();

        services.AddHttpClient(WebhookSender.HttpClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<WebhookOptions>>().Value;
                // The sender's own token enforces the per-attempt timeout; this is only a backstop.
                client.Timeout = options.Timeout + TimeSpan.FromSeconds(1);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("3dprintlog-webhooks/1");
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<WebhookOptions>>().Value;
                return WebhookHttpHandlerFactory.Create(
                    sp.GetRequiredService<WebhookAddressGuard>(), options.Timeout, options.MaxInFlightPerHost);
            })
            // The factory's logging handlers write the full request URI at Information, and a
            // webhook URL's path can be the receiver's secret. WebhookSender logs the host instead.
            .RemoveAllLoggers();

        return services;
    }

    /// <summary>
    /// Private addresses are reachable only when asked for <b>and</b> in Development, so a stray
    /// setting cannot open production to its own network.
    /// </summary>
    public static bool AllowPrivateAddresses(bool requested, IHostEnvironment environment) =>
        requested && environment.IsDevelopment();
}
