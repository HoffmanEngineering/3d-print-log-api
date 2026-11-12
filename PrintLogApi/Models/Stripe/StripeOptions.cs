using Microsoft.Extensions.Options;

namespace PrintLogApi.Models.Stripe;

/// <summary>The <c>Stripe</c> configuration section.</summary>
public class StripeOptions
{
    public const string SectionName = "Stripe";

    public string? SecretKey { get; set; }

    /// <summary>
    /// The signing secret (<c>whsec_…</c>) of the webhook endpoint in the Stripe Dashboard. Blank
    /// makes the webhook answer 503 to everything, and stops the app from starting in Production:
    /// Stripe.net accepts a payload signed with an empty key when the secret is empty, so a blank
    /// secret would otherwise let anyone post billing events.
    /// </summary>
    public string? WebhookSecret { get; set; }

    public string? ProMonthlyPriceId { get; set; }
    public string? ProAnnualPriceId { get; set; }
}

/// <summary>
/// Fails startup in Production without a webhook secret. Development, E2ETesting,
/// IntegrationTesting and staging run without Stripe, so a blank secret is allowed there and the
/// webhook answers 503 instead.
/// </summary>
public sealed class StripeOptionsValidator(IHostEnvironment environment) : IValidateOptions<StripeOptions>
{
    public ValidateOptionsResult Validate(string? name, StripeOptions options)
    {
        var failures = new List<string>();

        if (environment.IsProduction() && string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            failures.Add($"{StripeOptions.SectionName}:{nameof(StripeOptions.WebhookSecret)} is required in Production. Without it every Stripe webhook is refused.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class StripeServiceCollectionExtensions
{
    /// <summary>Binds <see cref="StripeOptions"/> and validates them when the host starts.</summary>
    public static IServiceCollection AddStripeOptions(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StripeOptions>>(new StripeOptionsValidator(environment));
        return services;
    }
}
