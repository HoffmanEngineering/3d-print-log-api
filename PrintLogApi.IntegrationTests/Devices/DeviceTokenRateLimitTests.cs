using System.Net;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.IntegrationTests.Controllers;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// A device token is a connector credential (#151): its writes spend a per-grant bucket, through
/// the same <c>ConnectorCredentialClaim</c> an API key carries, with no rule of its own.
/// </summary>
public class DeviceTokenRateLimitTests(ConnectorWriteRateLimitTests.LowConnectorBudgetFactory factory)
    : IClassFixture<ConnectorWriteRateLimitTests.LowConnectorBudgetFactory>
{
    private const int Burst = ConnectorWriteRateLimitTests.Burst;

    [Fact]
    public async Task DeviceWrites_BeyondTheBurst_Return429_PerGrant()
    {
        var client = factory.CreateClient();
        var subject = NewSubject("rate");
        var first = await TokenAsync(client, subject, DeviceScopes.All);
        var second = await TokenAsync(client, subject, DeviceScopes.All);
        var printerId = await CreatePrinterAsync(client, subject);
        HttpRequestMessage PrintWrite(string token) => PrintCreate(token, printerId, "Backfill");

        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < Burst + 1; i++)
        {
            responses.Add(await client.SendAsync(PrintWrite(first), TestContext.Current.CancellationToken));
        }

        Assert.All(responses.Take(Burst), r => Assert.True(r.IsSuccessStatusCode, $"expected 2xx, got {(int)r.StatusCode}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, responses[Burst].StatusCode);

        // A second bridge of the same user has its own bucket.
        var other = await client.SendAsync(PrintWrite(second), TestContext.Current.CancellationToken);
        Assert.True(other.IsSuccessStatusCode, $"expected 2xx, got {(int)other.StatusCode}");

        // And the heartbeat stays exempt (#200).
        var heartbeat = WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { Kind = "moonraker", DisplayName = "Rate limit bridge" }),
        }, first);
        var beat = await client.SendAsync(heartbeat, TestContext.Current.CancellationToken);
        Assert.True(beat.IsSuccessStatusCode, $"expected 2xx, got {(int)beat.StatusCode}");
    }
}
