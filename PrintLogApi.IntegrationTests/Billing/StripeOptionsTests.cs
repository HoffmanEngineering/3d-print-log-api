using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PrintLogApi.Models.Stripe;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Billing;

/// <summary>The Stripe settings' startup checks, and how a Stripe return URL's base is chosen.</summary>
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

    [Theory]
    [InlineData("localhost:4310")]
    [InlineData("ftp://localhost:4310")]
    [InlineData("https://localhost:4310/settings")]
    [InlineData("https://localhost:4310?x=1")]
    [InlineData("https://user@localhost:4310")]
    [InlineData("")]
    public void AnAllowedReturnOriginThatIsNotAnOrigin_FailsInEveryEnvironment(string entry)
    {
        var result = Validate("Development", new StripeOptions { AllowedReturnOrigins = [entry] });

        Assert.True(result.Failed);
        Assert.Contains("AllowedReturnOrigins", result.FailureMessage);
    }

    [Fact]
    public void AllowedReturnOrigins_AcceptsOriginsWithOrWithoutATrailingSlash()
    {
        Assert.True(Validate("Development", new StripeOptions { AllowedReturnOrigins = ["https://localhost:4310", "http://localhost:4311/"] }).Succeeded);
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
                ["Stripe:AllowedReturnOrigins:0"] = "https://localhost:4310",
            }).Build(),
            new TestHostEnvironment("Production"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
        var options = provider.GetRequiredService<IOptions<StripeOptions>>().Value;
        Assert.Equal("whsec_x", options.WebhookSecret);
        Assert.Equal(["https://localhost:4310"], options.AllowedReturnOrigins);
    }

    #endregion

    #region Return URL base

    private const string Web = "https://www.3dprintlog.com";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example")]
    [InlineData("https://www.3dprintlog.com.evil.example")]
    [InlineData("http://www.3dprintlog.com")]
    [InlineData("https://www.3dprintlog.com:444")]
    [InlineData("https://www.3dprintlog.com/elsewhere")]
    [InlineData("null")]
    [InlineData("not a url")]
    public void AnyOriginOtherThanAnAllowedOne_GetsTheConfiguredBase(string? origin)
    {
        Assert.Equal(Web, StripeReturnUrls.ResolveBase(origin, Web, ["https://localhost:4310"]));
    }

    [Theory]
    [InlineData("https://www.3dprintlog.com")]
    [InlineData("https://WWW.3dprintlog.com")]
    [InlineData("https://www.3dprintlog.com/")]
    public void TheConfiguredOrigin_GetsTheConfiguredBase(string origin)
    {
        Assert.Equal(Web, StripeReturnUrls.ResolveBase(origin, Web + "/", []));
    }

    [Fact]
    public void AnAllowListedOrigin_IsUsedAsTheBase()
    {
        Assert.Equal("https://localhost:4310", StripeReturnUrls.ResolveBase("https://localhost:4310", Web, ["https://localhost:4310/"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("www.3dprintlog.com")]
    public void WithoutAUsableConfiguredBase_ThereIsNoBase(string? webBase)
    {
        Assert.Null(StripeReturnUrls.ResolveBase("https://localhost:4310", webBase, ["https://localhost:4310"]));
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
