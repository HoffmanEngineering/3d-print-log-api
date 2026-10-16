using System.Net;
using System.Net.Sockets;

namespace PrintLogApi.Webhooks;

/// <summary>Where an address a webhook would connect to sits, for the SSRF guard (#235).</summary>
public enum WebhookAddressClass
{
    /// <summary>A routable internet address. Always allowed.</summary>
    Public,

    /// <summary>
    /// The machine itself or a private network: loopback, RFC 1918, CGNAT, link-local, unique-local
    /// IPv6. Blocked unless <c>Webhooks:AllowPrivateAddresses</c> is on, which only Development honors.
    /// </summary>
    Private,

    /// <summary>
    /// Never a legitimate webhook target, and blocked even when private addresses are allowed:
    /// unspecified, multicast, broadcast, reserved and documentation ranges, cloud metadata, and every
    /// IPv6 form that embeds an IPv4 address (mapped, compatible, NAT64, 6to4, Teredo), since those
    /// can smuggle a private IPv4 address past an IPv6-only check.
    /// </summary>
    Forbidden,
}

/// <summary>
/// Classifies the addresses a webhook may connect to. It is applied twice: to every resolved address
/// when a subscription is saved, and to the address actually connected to, in the handler's
/// <c>ConnectCallback</c>, so a DNS answer that changes after validation (rebinding) cannot reach an
/// internal address.
/// </summary>
public static class WebhookAddressPolicy
{
    // Most specific first: the metadata host sits inside link-local, which is only Private.
    private static readonly (IPNetwork Network, WebhookAddressClass Class)[] V4Ranges =
    [
        (IPNetwork.Parse("169.254.169.254/32"), WebhookAddressClass.Forbidden), // cloud metadata
        (IPNetwork.Parse("0.0.0.0/8"), WebhookAddressClass.Forbidden), // "this network", unspecified
        (IPNetwork.Parse("10.0.0.0/8"), WebhookAddressClass.Private),
        (IPNetwork.Parse("100.64.0.0/10"), WebhookAddressClass.Private), // CGNAT
        (IPNetwork.Parse("127.0.0.0/8"), WebhookAddressClass.Private), // loopback
        (IPNetwork.Parse("169.254.0.0/16"), WebhookAddressClass.Private), // link-local
        (IPNetwork.Parse("172.16.0.0/12"), WebhookAddressClass.Private),
        (IPNetwork.Parse("192.0.0.0/24"), WebhookAddressClass.Forbidden), // IETF protocol assignments
        (IPNetwork.Parse("192.0.2.0/24"), WebhookAddressClass.Forbidden), // TEST-NET-1
        (IPNetwork.Parse("192.88.99.0/24"), WebhookAddressClass.Forbidden), // 6to4 relay anycast
        (IPNetwork.Parse("192.168.0.0/16"), WebhookAddressClass.Private),
        (IPNetwork.Parse("198.18.0.0/15"), WebhookAddressClass.Forbidden), // benchmarking
        (IPNetwork.Parse("198.51.100.0/24"), WebhookAddressClass.Forbidden), // TEST-NET-2
        (IPNetwork.Parse("203.0.113.0/24"), WebhookAddressClass.Forbidden), // TEST-NET-3
        (IPNetwork.Parse("224.0.0.0/4"), WebhookAddressClass.Forbidden), // multicast
        (IPNetwork.Parse("240.0.0.0/4"), WebhookAddressClass.Forbidden), // reserved, broadcast
    ];

    private static readonly (IPNetwork Network, WebhookAddressClass Class)[] V6Ranges =
    [
        (IPNetwork.Parse("::1/128"), WebhookAddressClass.Private), // loopback
        (IPNetwork.Parse("::/128"), WebhookAddressClass.Forbidden), // unspecified
        (IPNetwork.Parse("::/96"), WebhookAddressClass.Forbidden), // IPv4-compatible (deprecated)
        (IPNetwork.Parse("64:ff9b::/96"), WebhookAddressClass.Forbidden), // NAT64
        (IPNetwork.Parse("64:ff9b:1::/48"), WebhookAddressClass.Forbidden), // local-use NAT64
        (IPNetwork.Parse("100::/64"), WebhookAddressClass.Forbidden), // discard
        (IPNetwork.Parse("2001::/32"), WebhookAddressClass.Forbidden), // Teredo
        (IPNetwork.Parse("2001:db8::/32"), WebhookAddressClass.Forbidden), // documentation
        (IPNetwork.Parse("2002::/16"), WebhookAddressClass.Forbidden), // 6to4
        (IPNetwork.Parse("fd00:ec2::254/128"), WebhookAddressClass.Forbidden), // AWS metadata
        (IPNetwork.Parse("fc00::/7"), WebhookAddressClass.Private), // unique local
        (IPNetwork.Parse("fe80::/10"), WebhookAddressClass.Private), // link-local
        (IPNetwork.Parse("fec0::/10"), WebhookAddressClass.Forbidden), // site-local (deprecated)
        (IPNetwork.Parse("ff00::/8"), WebhookAddressClass.Forbidden), // multicast
    ];

    public static WebhookAddressClass Classify(IPAddress address)
    {
        var ranges = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => V4Ranges,
            // The scope id (fe80::1%3) is not part of the address's range.
            AddressFamily.InterNetworkV6 => V6Ranges,
            _ => null,
        };
        // Checked by property, not by range: IPNetwork.Contains treats a mapped address as its
        // IPv4 form and so never matches ::ffff:0:0/96.
        if (ranges is null || address.IsIPv4MappedToIPv6)
        {
            return WebhookAddressClass.Forbidden;
        }

        var unscoped = address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address;

        foreach (var (network, cls) in ranges)
        {
            if (network.Contains(unscoped))
            {
                return cls;
            }
        }
        return WebhookAddressClass.Public;
    }

    /// <summary>True when a webhook may connect to <paramref name="address"/>.</summary>
    public static bool IsAllowed(IPAddress address, bool allowPrivate) => Classify(address) switch
    {
        WebhookAddressClass.Public => true,
        WebhookAddressClass.Private => allowPrivate,
        _ => false,
    };
}
