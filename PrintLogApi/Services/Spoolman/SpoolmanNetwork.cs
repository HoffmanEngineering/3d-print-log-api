using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PrintLogApi.Services.Spoolman;

/// <summary>
/// Reduces a connecting address to the network it belongs to, for the fair-use network count
/// (#175). Hosts on one home or office network share a /24 (IPv4) or, for IPv6, the /48 an ISP
/// typically delegates to one site, so a printer whose address shifts within it stays one network.
/// </summary>
public static class SpoolmanNetwork
{
    /// <summary>The network prefix in CIDR form, e.g. <c>203.0.113.0/24</c> or <c>2001:db8:1::/48</c>.</summary>
    public static string Prefix(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
            return $"{new IPAddress(bytes)}/24";
        }

        Array.Clear(bytes, 6, bytes.Length - 6);
        return $"{new IPAddress(bytes)}/48";
    }

    /// <summary>
    /// Keyed hash of <see cref="Prefix"/>, lower-case hex. Keyed because the IPv4 space is small:
    /// a bare hash of a /24 is reversed by hashing all 2^24 of them, which takes seconds.
    /// </summary>
    public static string Hash(IPAddress address, byte[] key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Prefix(address))));
}
