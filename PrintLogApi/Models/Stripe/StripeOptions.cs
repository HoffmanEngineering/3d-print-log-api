using Microsoft.Extensions.Options;
using PrintLogApi.Services;

namespace PrintLogApi.Models.Stripe;

/// <summary>The <c>Stripe</c> configuration section.</summary>
public class StripeOptions
{
    public const string SectionName = "Stripe";

    public string? SecretKey { get; set; }

    /// <summary>
    /// The signing secret (<c>whsec_…</c>) of the webhook endpoint in the Stripe Dashboard. Blank
    /// makes the webhook answer 503 to everything: Stripe.net accepts a payload signed with an empty
    /// key when the secret is empty, so a blank secret would otherwise let anyone post billing
    /// events. In Production a blank secret is also logged at Critical when the host starts
    /// (<see cref="StripeWebhookSecretCheck"/>); it does not stop the host, because the webhook
    /// already fails closed and a missing app setting should not take the whole API down.
    /// </summary>
    public string? WebhookSecret { get; set; }

    public string? ProMonthlyPriceId { get; set; }
    public string? ProAnnualPriceId { get; set; }

    /// <summary>
    /// Origins (<c>https://localhost:4310</c>), besides <c>Email:WebBaseUrl</c>'s, that Stripe may
    /// send a customer back to after checkout or the billing portal, when the request comes from
    /// one of them. For Development, where a UI worktree runs on its own port. Empty by default,
    /// and production needs nothing here: every other request returns to <c>Email:WebBaseUrl</c>.
    /// </summary>
    public string[] AllowedReturnOrigins { get; set; } = [];
}

/// <summary>
/// Fails startup, in every environment, on an <see cref="StripeOptions.AllowedReturnOrigins"/> entry
/// that is not an origin. That key is new, so no existing deployment can have a bad one. A blank
/// webhook secret is deliberately not a startup failure (see <see cref="StripeWebhookSecretCheck"/>).
/// </summary>
public sealed class StripeOptionsValidator : IValidateOptions<StripeOptions>
{
    public ValidateOptionsResult Validate(string? name, StripeOptions options)
    {
        var failures = new List<string>();

        foreach (var entry in options.AllowedReturnOrigins)
        {
            if (!StripeReturnUrls.TryNormalizeOrigin(entry, out _))
            {
                failures.Add($"{StripeOptions.SectionName}:{nameof(StripeOptions.AllowedReturnOrigins)} entries must be http or https origins with no path, query or user info, such as https://localhost:4310.");
                break;
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Writes one Critical log entry when the host starts in Production without
/// <c>Stripe:WebhookSecret</c>, so a missing app setting is visible in the logs and alerts instead of
/// only as 503s in the Stripe Dashboard. It never stops the host: the webhook already refuses every
/// event without a secret, so failing startup would add no protection and turn a missing setting
/// into a full API outage.
/// </summary>
public sealed class StripeWebhookSecretCheck(
    IOptions<StripeOptions> options,
    ILogger<StripeWebhookSecretCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.WebhookSecret))
        {
            logger.LogCritical("Stripe:WebhookSecret is not configured; Stripe webhooks are refused until it is set.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class StripeServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="StripeOptions"/> and validates them when the host starts. In Production it
    /// also registers <see cref="StripeWebhookSecretCheck"/>. Development, E2ETesting,
    /// IntegrationTesting and staging run without Stripe, so a blank secret is not worth a Critical
    /// entry there; their webhook answers 503 all the same.
    /// </summary>
    public static IServiceCollection AddStripeOptions(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StripeOptions>, StripeOptionsValidator>();
        if (environment.IsProduction())
        {
            services.AddHostedService<StripeWebhookSecretCheck>();
        }
        return services;
    }
}
