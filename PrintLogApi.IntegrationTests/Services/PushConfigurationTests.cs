using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Services.Push;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class PushConfigurationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PushConfigurationTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void WithoutCredentials_ResolvesNoOpClient_AndDoesNotFailStartup()
    {
        using var scope = _factory.Services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IFcmClient>();

        Assert.IsType<NoOpFcmClient>(client);
    }

    [Fact]
    public async Task NoOpClient_ReportsFailureWithoutThrowing()
    {
        var client = new NoOpFcmClient(NullLogger<NoOpFcmClient>.Instance);

        var result = await client.SendAsync(
            [new FcmMessage("tok", "Title", "Body", new Dictionary<string, string>())],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.SuccessCount);
        Assert.Empty(result.UnregisteredTokens);
    }

    [Fact]
    public async Task ReadinessReportsDegraded_WhenPushIsUnconfigured()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The check must carry the ready tag, or it appears on no endpoint at all and a
        // misconfigured deployment looks perfectly healthy while every push is dropped.
        Assert.Contains("push", body, StringComparison.OrdinalIgnoreCase);
    }
}
