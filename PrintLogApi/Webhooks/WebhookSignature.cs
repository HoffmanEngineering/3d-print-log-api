using System.Security.Cryptography;
using System.Text;

namespace PrintLogApi.Webhooks;

/// <summary>
/// The <c>X-PrintLog-Signature</c> header (#235), Stripe's scheme: <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>,
/// where the hex is HMAC-SHA256 keyed with the secret's UTF-8 bytes over <c>t + "." + body</c>. The
/// timestamp is inside the MAC, so a receiver that rejects old timestamps also rejects replays.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-PrintLog-Signature";

    public static string Create(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }
}
