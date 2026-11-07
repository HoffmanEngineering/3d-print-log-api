using Microsoft.Extensions.FileProviders;
using PrintLogApi.Telemetry;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

/// <summary>
/// Which environments send to Application Insights. A developer machine sent its own requests
/// and its SQL calls to localhost,1434 to the production resource, because appsettings.json
/// carries the production key and Development did not switch it off.
/// </summary>
public class TelemetryEnvironmentTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PrintLogApi";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(string? enableInDevelopment = null)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TelemetryEnvironment.EnableInDevelopmentKey] = enableInDevelopment,
            })
            .Build();

    [Theory]
    [InlineData("IntegrationTesting")]
    [InlineData("E2ETesting")]
    [InlineData("Development")]
    public void Is_off_in_test_and_development_environments(string environment)
    {
        Assert.True(TelemetryEnvironment.IsDisabled(new Environment(environment), Config()));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Is_on_in_deployed_environments(string environment)
    {
        Assert.False(TelemetryEnvironment.IsDisabled(new Environment(environment), Config()));
    }

    [Fact]
    public void Development_sends_only_when_opted_in()
    {
        Assert.False(TelemetryEnvironment.IsDisabled(new Environment("Development"), Config("true")));
        Assert.True(TelemetryEnvironment.IsDisabled(new Environment("Development"), Config("false")));
    }

    [Theory]
    [InlineData("IntegrationTesting")]
    [InlineData("E2ETesting")]
    public void The_development_opt_in_does_not_reach_the_test_environments(string environment)
    {
        Assert.True(TelemetryEnvironment.IsDisabled(new Environment(environment), Config("true")));
    }
}
