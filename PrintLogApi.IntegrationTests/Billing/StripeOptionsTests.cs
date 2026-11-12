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

    private static ValidateOptionsResult Validate(StripeOptions options) =>
        new StripeOptionsValidator().Validate(null, options);

    private static ServiceProvider Build(string environment, Dictionary<string, string?> settings, LevelCapturingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (logs is not null) b.AddProvider(logs);
        });
        services.AddStripeOptions(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new TestHostEnvironment(environment));
        return services.BuildServiceProvider();
    }

    private static async Task StartHostedServicesAsync(IServiceProvider provider)
    {
        provider.GetRequiredService<IStartupValidator>().Validate();
        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A blank secret never stops the host, in any environment: the webhook already fails closed
    /// (503), and a missing app setting must not turn a deploy into a full API outage.
    /// </summary>
    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "")]
    [InlineData("Production", "   ")]
    [InlineData("Development", "")]
    [InlineData("E2ETesting", "")]
    [InlineData("IntegrationTesting", "")]
    [InlineData("Staging", "")]
    public void ABlankWebhookSecret_DoesNotStopStartup(string environment, string? secret)
    {
        using var provider = Build(environment, new() { ["Stripe:WebhookSecret"] = secret });

        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.True(Validate(new StripeOptions { WebhookSecret = secret }).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Production_WithoutAWebhookSecret_LogsOneCriticalEntryAtStartup(string? secret)
    {
        var logs = new LevelCapturingLoggerProvider();
        using var provider = Build("Production", new() { ["Stripe:WebhookSecret"] = secret }, logs);

        await StartHostedServicesAsync(provider);

        var entry = Assert.Single(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains("Stripe:WebhookSecret is not configured", entry.Message);
    }

    [Fact]
    public async Task Production_WithAWebhookSecret_LogsNothingAndNeverTheSecret()
    {
        var logs = new LevelCapturingLoggerProvider();
        using var provider = Build("Production", new() { ["Stripe:WebhookSecret"] = "whsec_do_not_log" }, logs);

        await StartHostedServicesAsync(provider);

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("whsec_do_not_log", StringComparison.Ordinal));
    }

    /// <summary>The environments that run without Stripe on purpose get no Critical entry for it.</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("E2ETesting")]
    [InlineData("IntegrationTesting")]
    [InlineData("Staging")]
    public async Task OtherEnvironments_DoNotLogABlankWebhookSecretAsCritical(string environment)
    {
        var logs = new LevelCapturingLoggerProvider();
        using var provider = Build(environment, new() { ["Stripe:WebhookSecret"] = "" }, logs);

        await StartHostedServicesAsync(provider);

        Assert.DoesNotContain(provider.GetServices<IHostedService>(), s => s is StripeWebhookSecretCheck);
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);
    }

    [Theory]
    [InlineData("localhost:4310")]
    [InlineData("ftp://localhost:4310")]
    [InlineData("https://localhost:4310/settings")]
    [InlineData("https://localhost:4310?x=1")]
    [InlineData("https://user@localhost:4310")]
    [InlineData("")]
    public void AnAllowedReturnOriginThatIsNotAnOrigin_FailsValidation(string entry)
    {
        var result = Validate(new StripeOptions { AllowedReturnOrigins = [entry] });

        Assert.True(result.Failed);
        Assert.Contains("AllowedReturnOrigins", result.FailureMessage);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void AnAllowedReturnOriginThatIsNotAnOrigin_StopsStartup(string environment)
    {
        using var provider = Build(environment, new()
        {
            ["Stripe:WebhookSecret"] = "whsec_x",
            ["Stripe:AllowedReturnOrigins:0"] = "https://localhost:4310/settings",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("AllowedReturnOrigins", ex.Message);
    }

    [Fact]
    public void AllowedReturnOrigins_AcceptsOriginsWithOrWithoutATrailingSlash()
    {
        Assert.True(Validate(new StripeOptions { AllowedReturnOrigins = ["https://localhost:4310", "http://localhost:4311/"] }).Succeeded);
    }

    [Fact]
    public void Registration_BindsTheStripeSection()
    {
        using var provider = Build("Production", new()
        {
            ["Stripe:WebhookSecret"] = "whsec_x",
            ["Stripe:AllowedReturnOrigins:0"] = "https://localhost:4310",
        });

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

    /// <summary>Records each entry with its level.</summary>
    public sealed class LevelCapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyCollection<(LogLevel Level, string Message)> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose() { }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PrintLogApi";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
