using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email.Campaigns;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// Which address the pairing notice names when the request arrives through a proxy that sets
/// <c>X-Forwarded-For</c>, under the app's real configuration: nothing in <c>Startup</c> reads
/// forwarded headers, so only the host's <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> switch
/// (host setting <c>FORWARDEDHEADERS_ENABLED</c>) makes the client's address visible.
/// </summary>
public static class DevicePairingForwardedAddress
{
    public const string Client = "198.51.100.20";

    public static async Task<string?> NoticedAddressAsync(CustomWebApplicationFactory app, string label)
    {
        var client = app.CreateClient();
        var subject = NewSubject(label);
        var request = PairRequest(PairingToken(subject), Guid.NewGuid());
        request.Headers.Add("X-Forwarded-For", Client);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);

        var userId = await Bridges.BridgeTestKit.UserIdAsync(app, subject);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = await db.EmailOutbox.AsNoTracking().SingleAsync(o => o.UserId == userId && o.Campaign == DevicePairedCampaign.CampaignName, TestContext.Current.CancellationToken);
        return (string?)JsonNode.Parse(row.Exposure!)!["ipAddress"];
    }

    public sealed class ForwardedHeadersEnabledFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FORWARDEDHEADERS_ENABLED", "true");
        }
    }

    public class WithoutTheSwitch(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
    {
        [Fact]
        public async Task XForwardedFor_IsIgnored()
            => Assert.Null(await NoticedAddressAsync(factory, "xff-off"));
    }

    public class WithTheSwitch(ForwardedHeadersEnabledFactory factory) : IClassFixture<ForwardedHeadersEnabledFactory>
    {
        [Fact]
        public async Task TheForwardedClientAddress_IsNamed()
            => Assert.Equal(Client, await NoticedAddressAsync(factory, "xff-on"));
    }
}
