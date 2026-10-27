using System.Security.Cryptography;
using System.Text;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// The reference sealer and opener for <c>printlog-credential</c> v1
/// (<c>docs/bridge-credential-sealing.md</c>), in .NET. The API never seals or opens; this is the
/// browser's half and the bridge's half, so the tests can prove the vectors and round-trips.
/// </summary>
public static class CredentialSealer
{
    public static readonly byte[] Info = Encoding.ASCII.GetBytes("printlog-credential:v1:aes-256-gcm");

    public static string AssociatedData(long bridgeId, long entryId, string field) => $"printlog-credential:v1:{bridgeId}:{entryId}:{field}";

    public static byte[] Raw(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return [0x04, .. q.X!, .. q.Y!];
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string text)
        => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));

    /// <summary>Seals <paramref name="plaintext"/> to the SPKI public key, as the browser does.</summary>
    public static string Seal(string publicKeySpkiBase64, string associatedData, string plaintext, ECDiffieHellman? ephemeral = null, byte[]? nonce = null)
    {
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
        var ownEphemeral = ephemeral is null;
        ephemeral ??= ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var ephRaw = Raw(ephemeral);
            var shared = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [.. ephRaw, .. Raw(recipient)], Info);
            nonce ??= RandomNumberGenerator.GetBytes(12);
            var pt = Encoding.UTF8.GetBytes(plaintext);
            var ct = new byte[pt.Length];
            var tag = new byte[16];
            using (var gcm = new AesGcm(key, 16))
            {
                gcm.Encrypt(nonce, pt, ct, tag, Encoding.ASCII.GetBytes(associatedData));
            }

            return "v1." + Base64Url([.. ephRaw, .. nonce, .. ct, .. tag]);
        }
        finally
        {
            if (ownEphemeral)
            {
                ephemeral.Dispose();
            }
        }
    }

    /// <summary>Opens a sealed value with the bridge's private key, as the bridge does. Throws on any failure.</summary>
    public static string Open(ECDiffieHellman bridgeKey, string sealedValue, string associatedData)
    {
        if (!sealedValue.StartsWith("v1.", StringComparison.Ordinal))
        {
            throw new CryptographicException("not a v1 sealed value");
        }

        var blob = FromBase64Url(sealedValue[3..]);
        var ephRaw = blob[..65];
        using var ephemeral = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = ephRaw[1..33], Y = ephRaw[33..65] },
        });
        var shared = bridgeKey.DeriveRawSecretAgreement(ephemeral.PublicKey);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [.. ephRaw, .. Raw(bridgeKey)], Info);
        var body = blob[77..];
        var ct = body[..^16];
        var pt = new byte[ct.Length];
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Decrypt(blob[65..77], ct, body[^16..], pt, Encoding.ASCII.GetBytes(associatedData));
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(pt);
    }

    /// <summary>A P-256 key from its private scalar and its uncompressed public point, both hex.</summary>
    public static ECDiffieHellman FromHex(string privateHex, string publicRawHex)
    {
        var raw = Convert.FromHexString(publicRawHex);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromHexString(privateHex),
            Q = new ECPoint { X = raw[1..33], Y = raw[33..65] },
        });
    }
}
