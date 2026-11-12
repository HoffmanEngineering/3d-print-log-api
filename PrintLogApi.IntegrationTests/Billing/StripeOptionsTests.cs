using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PrintLogApi.Models.Stripe;
using Xunit;

namespace PrintLogApi.IntegrationTests.Billing;

/// <summary>The Stripe settings' startup checks.</summary>
public class StripeOptionsTests
{
    #region Startup validation

    private static ValidateOptionsResult Validate(string environment, StripeOptions options) =>
        new StripeOptionsValidator(new TestHostEnvironment(environment)).Validate(null, options);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Production_RequiresAWebhookSecret(string? secret)
    {
        var result = Validate("Production", new StripeOptions { WebhookSecret = secret });

        Assert.True(result.Failed);
        Assert.Contains("WebhookSecret", result.FailureMessage);
    }

    [Fact]
    public void Production_WithAWebhookSecret_IsValid()
    {
        Assert.True(Validate("Production", new StripeOptions { WebhookSecret = "whsec_x" }).Succeeded);
    }

    /// <summary>
    /// These run without Stripe on purpose: local development, the Cypress API, the integration
    /// test host, and a staging slot without a Stripe section. Their webhook answers 503 instead.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("E2ETesting")]
    [InlineData("IntegrationTesting")]
    [InlineData("Staging")]
    public void EnvironmentsWithoutStripe_StartWithABlankWebhookSecret(string environment)
    {
        Assert.True(Validate(environment, new StripeOptions { WebhookSecret = "" }).Succeeded);
    }

    [Fact]
    public void Registration_ValidatesOnStart_SoProductionWithoutASecretDoesNotStart()
    {
        var services = new ServiceCollection();
        services.AddStripeOptions(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:WebhookSecret"] = "" }).Build(),
            new TestHostEnvironment("Production"));
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("WebhookSecret", ex.Message);
    }

    [Fact]
    public void Registration_BindsTheStripeSection()
    {
        var services = new ServiceCollection();
        services.AddStripeOptions(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:WebhookSecret"] = "whsec_x",
            }).Build(),
            new TestHostEnvironment("Production"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<StripeOptions>>().Value;
        Assert.Equal("whsec_x", options.WebhookSecret);
    }

    #endregion

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PrintLogApi";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
