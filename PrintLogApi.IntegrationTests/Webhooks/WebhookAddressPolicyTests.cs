using System.Net;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// The address classifier behind the webhook SSRF guard (#235). Every range is pinned by name, so
/// removing one from the policy fails here rather than opening a hole.
/// </summary>
public class WebhookAddressPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.215.14")]
    [InlineData("100.63.255.255")] // just below CGNAT
    [InlineData("100.128.0.0")] // just above CGNAT
    [InlineData("172.15.255.255")] // just below 172.16/12
    [InlineData("172.32.0.0")] // just above 172.16/12
    [InlineData("192.169.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void PublicAddresses_ArePublic(string address)
    {
        Assert.Equal(WebhookAddressClass.Public, WebhookAddressPolicy.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")] // loopback
    [InlineData("127.255.255.254")]
    [InlineData("10.0.0.1")] // RFC 1918
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.0.1")] // CGNAT (Tailscale lives here)
    [InlineData("100.127.255.255")]
    [InlineData("169.254.1.1")] // link-local
    [InlineData("::1")] // IPv6 loopback
    [InlineData("fc00::1")] // unique local
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fe80::1")] // link-local
    [InlineData("febf::1")]
    public void PrivateAddresses_ArePrivate(string address)
    {
        Assert.Equal(WebhookAddressClass.Private, WebhookAddressPolicy.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("fd00:ec2::254")] // AWS metadata over IPv6
    [InlineData("0.0.0.0")] // unspecified
    [InlineData("0.1.2.3")] // "this network"
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("224.0.0.1")] // multicast
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")] // reserved
    [InlineData("192.0.0.1")] // IETF protocol assignments
    [InlineData("192.0.2.1")] // TEST-NET-1
    [InlineData("198.51.100.1")] // TEST-NET-2
    [InlineData("203.0.113.1")] // TEST-NET-3
    [InlineData("198.18.0.1")] // benchmarking
    [InlineData("198.19.255.255")]
    [InlineData("::")] // unspecified
    [InlineData("ff02::1")] // multicast
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped
    [InlineData("::ffff:8.8.8.8")] // IPv4-mapped, even a public one
    [InlineData("::127.0.0.1")] // IPv4-compatible
    [InlineData("64:ff9b::a00:1")] // NAT64 of 10.0.0.1
    [InlineData("64:ff9b:1::1")] // local-use NAT64
    [InlineData("2002:a00:1::1")] // 6to4 of 10.0.0.1
    [InlineData("2001:0:4136:e378::1")] // Teredo
    [InlineData("2001:db8::1")] // documentation
    [InlineData("100::1")] // discard
    [InlineData("fec0::1")] // deprecated site-local
    public void ForbiddenAddresses_AreForbidden(string address)
    {
        Assert.Equal(WebhookAddressClass.Forbidden, WebhookAddressPolicy.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8", false, true)]
    [InlineData("8.8.8.8", true, true)]
    [InlineData("192.168.1.10", false, false)]
    [InlineData("192.168.1.10", true, true)]
    [InlineData("127.0.0.1", true, true)]
    [InlineData("169.254.169.254", true, false)]
    [InlineData("0.0.0.0", true, false)]
    [InlineData("::ffff:192.168.1.10", true, false)]
    public void IsAllowed_LetsPrivateThroughOnlyWhenAllowed_AndNeverForbidden(string address, bool allowPrivate, bool expected)
    {
        Assert.Equal(expected, WebhookAddressPolicy.IsAllowed(IPAddress.Parse(address), allowPrivate));
    }

    [Fact]
    public void AScopedLinkLocalAddress_IsStillPrivate()
    {
        var address = IPAddress.Parse("fe80::1%3");
        Assert.Equal(WebhookAddressClass.Private, WebhookAddressPolicy.Classify(address));
    }
}
