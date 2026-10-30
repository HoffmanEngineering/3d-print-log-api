using System.Net;
using System.Net.Sockets;

namespace PrintLogApi.Services.DeviceGrants;

/// <summary>
/// The address a pairing notice names (api#287): the request's client address as the app sees
/// it, written the way people read it, or nothing when it can't be the client's.
/// </summary>
public static class PairingNoticeAddress
{
    /// <summary>
    /// <paramref name="address"/> as text, an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>)
    /// as plain IPv4; null for no address, and for a private, shared (100.64/10), loopback,
    /// link-local or unspecified one. Behind App Service without forwarded headers the peer is
    /// the front end's private address, and naming it in a security notice would mislead.
    /// </summary>
    public static string? For(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IsPublic(address) ? address.ToString() : null;
    }

    private static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || b[0] == 0);
        }

        // fc00::/7 (unique local) and fe80::/10 (link-local); site-local is deprecated but private.
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal);
    }
}
