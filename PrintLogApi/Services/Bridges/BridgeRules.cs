using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PrintLogApi.Models;

namespace PrintLogApi.Services.Bridges;

/// <summary>
/// What a bridge may report and what the user may point a bridge at (printlog-bridge#100): private
/// addresses only, small fixed shapes, no free-form blobs. Each check returns a machine-readable
/// code on failure, or null.
/// </summary>
public static partial class BridgeRules
{
    public const int TextMaxLength = 100;
    public const int IdMaxLength = 64;
    public const int MaxMacs = 8;

    /// <summary>
    /// Host-name suffixes a home network resolves locally. A host name outside them (or a single
    /// label) could point the bridge anywhere on the internet.
    /// </summary>
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home.arpa", ".internal", ".localdomain"];

    /// <summary>
    /// Whether <paramref name="address"/> is on a network the bridge's LAN can own: IPv4 RFC 1918
    /// and link-local, IPv6 unique-local. Not loopback, CGNAT, IPv6 link-local (useless without a
    /// zone) or anything public.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6)
        {
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    /// <summary>
    /// Parses a literal IP address strictly: dotted-quad IPv4 or IPv6 without a zone. Returns the
    /// canonical text, or a code: <c>invalid_address</c> or <c>address_not_private</c>.
    /// </summary>
    public static (string? Address, string? Code) Address(string? text)
    {
        if (!TryParseLiteral(text, out var address))
        {
            return (null, "invalid_address");
        }

        return IsPrivate(address) ? (address.ToString(), null) : (null, "address_not_private");
    }

    private static bool TryParseLiteral(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrEmpty(text) || text.Length > 45)
        {
            return false;
        }

        // IPAddress.TryParse also takes "10.1", "0x0a000001" and zone ids; none of those is a
        // shape a bridge has any reason to send.
        var shapeOk = text.Contains(':') ? Ipv6Shape().IsMatch(text) : Ipv4Shape().IsMatch(text);
        if (!shapeOk || !IPAddress.TryParse(text, out var parsed))
        {
            return false;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork && text.Split('.').Any(octet => int.Parse(octet, CultureInfo.InvariantCulture) > 255))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>
    /// A network the bridge sees: a private CIDR, IPv4 /24 to /32 or IPv6 /64 to /128. Returns it
    /// normalized to the network address, or <c>invalid_network</c>, <c>network_not_private</c>
    /// or <c>network_too_large</c>.
    /// </summary>
    public static (string? Network, string? Code) Network(string? text)
    {
        var parts = text?.Split('/') ?? [];
        if (parts.Length != 2
            || !TryParseLiteral(parts[0], out var address)
            || !PrefixShape().IsMatch(parts[1])
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            return (null, "invalid_network");
        }

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var smallest = address.AddressFamily == AddressFamily.InterNetwork ? 24 : 64;
        if (prefix > bits)
        {
            return (null, "invalid_network");
        }

        if (!IsPrivate(address))
        {
            return (null, "network_not_private");
        }

        if (prefix < smallest)
        {
            return (null, "network_too_large");
        }

        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return ($"{new IPAddress(bytes)}/{prefix.ToString(CultureInfo.InvariantCulture)}", null);
    }

    /// <summary>A MAC address, normalized to <c>aa:bb:cc:dd:ee:ff</c>; not zero, broadcast or multicast.</summary>
    public static string? Mac(string? text)
    {
        if (text is null || !MacShape().IsMatch(text))
        {
            return null;
        }

        var hex = text.Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var first = Convert.ToByte(hex[..2], 16);
        if (hex == "000000000000" || (first & 1) == 1)
        {
            return null;
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>A serial number or mainboard id: 1 to 64 of letters, digits and <c>. _ : -</c>.</summary>
    public static bool IsId(string? text) => text is not null && IdShape().IsMatch(text);

    /// <summary>
    /// Display text the bridge read off a printer (its name, its model). Trimmed; empty is null.
    /// False when it is too long or has control characters.
    /// </summary>
    public static bool TryText(string? text, out string? value, int maxLength = TextMaxLength)
    {
        value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        return value is null || (value.Length <= maxLength && !value.Any(char.IsControl));
    }

    public static bool IsPort(int? port) => port is null or (>= 1 and <= 65535);

    /// <summary>The adapter kind, lower-case, or null when it is not one of <see cref="Connection.BridgeKinds"/>.</summary>
    public static string? Kind(string? kind) => Connection.IsBridgeKind(kind?.Trim()) ? kind!.Trim().ToLowerInvariant() : null;

    /// <summary>
    /// Where the user points a bridge: a private IP literal, or a host name that is a single label
    /// or ends in a local suffix. Returns it lower-cased, or null. The bridge still checks that a
    /// host name resolves to a private address before it connects.
    /// </summary>
    public static string? Host(string? text)
    {
        var host = text?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        if (TryParseLiteral(host, out var address))
        {
            return IsPrivate(address) ? address.ToString() : null;
        }

        host = host.ToLowerInvariant();
        if (host.Length > 253 || !host.Split('.').All(label => HostLabel().IsMatch(label)))
        {
            return null;
        }

        // An all-digit last label ("10", "192.168.1.300") is an address to some resolvers.
        if (host.Split('.')[^1].All(char.IsAsciiDigit))
        {
            return null;
        }

        return !host.Contains('.') || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)) ? host : null;
    }

    /// <summary>
    /// The stable identity of a discovered printer, so the same printer is one row across
    /// check-ins and address changes: its serial, else its mainboard id, else its lowest MAC with
    /// the port (two Moonraker instances on one host share MACs), else its address and port.
    /// </summary>
    public static string IdentityKey(string kind, string? serial, string? mainboardId, IReadOnlyList<string> macs, string? address, int? port)
    {
        var portText = (port ?? 0).ToString(CultureInfo.InvariantCulture);
        if (serial is not null)
        {
            return $"{kind}:sn:{serial.ToUpperInvariant()}";
        }

        if (mainboardId is not null)
        {
            return $"{kind}:mb:{mainboardId.ToLowerInvariant()}";
        }

        return macs.Count > 0
            ? $"{kind}:mac:{macs.Order(StringComparer.Ordinal).First()}:{portText}"
            : $"{kind}:ip:{address}:{portText}";
    }

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")]
    private static partial Regex Ipv4Shape();

    [GeneratedRegex(@"^[0-9A-Fa-f:.]{2,45}$")]
    private static partial Regex Ipv6Shape();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex PrefixShape();

    [GeneratedRegex(@"^(?:[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5}|[0-9A-Fa-f]{12})$")]
    private static partial Regex MacShape();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex IdShape();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostLabel();
}
