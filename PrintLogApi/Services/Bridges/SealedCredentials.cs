using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PrintLogApi.Services.Bridges;

/// <summary>
/// The API's half of <c>printlog-credential</c> v1 (docs/bridge-credential-sealing.md): LAN
/// credentials sealed in the browser to a bridge's P-256 key (ECDH, HKDF-SHA256, AES-256-GCM).
/// The API never decrypts and holds no key that could. It parses the bridge's public key
/// strictly and checks the shape of each sealed value, nothing more.
/// </summary>
public static partial class SealedCredentials
{
    /// <summary>The DER SubjectPublicKeyInfo prefix of an uncompressed P-256 key (id-ecPublicKey, prime256v1).</summary>
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("3059301306072a8648ce3d020106082a8648ce3d03010703420004");

    private static readonly BigInteger P = Unsigned("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");

    private static readonly BigInteger B = Unsigned("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");

    public const string Version = "v1";

    /// <summary>An uncompressed point, a nonce and a tag around 1 to 256 bytes of plaintext.</summary>
    public const int MinSealedBytes = 65 + 12 + 1 + 16;

    public const int MaxPlaintextBytes = 256;

    public const int MaxSealedBytes = 65 + 12 + MaxPlaintextBytes + 16;

    /// <summary>The longest sealed value as text: <c>v1.</c> and unpadded base64url of <see cref="MaxSealedBytes"/>.</summary>
    public const int MaxSealedLength = 3 + (MaxSealedBytes * 4 + 2) / 3;

    /// <summary>The public key on the wire: base64 of the 91-byte SPKI, always 124 characters.</summary>
    public const int PublicKeyLength = 124;

    public const int FingerprintLength = 64;

    /// <summary>What a bridge may report about a credential it can't use.</summary>
    public static readonly IReadOnlySet<string> IssueCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "missing", "undecryptable", "invalid_plaintext", "rejected_by_printer",
    };

    /// <summary>
    /// The fields each adapter takes: printlog-bridge's <c>app_secrets</c>. A kind with no adapter
    /// that needs a secret takes none.
    /// </summary>
    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["bambu"] = ["access_code"],
        ["prusalink"] = ["password", "username", "api_key"],
        ["moonraker"] = ["moonraker_api_key"],
        ["octoprint"] = ["api_key"],
        ["sdcp"] = [],
    };

    public static IReadOnlyList<string> FieldsFor(string? kind) => kind is not null && Fields.TryGetValue(kind, out var fields) ? fields : [];

    public static bool IsField(string? kind, string? field) => field is not null && FieldsFor(kind).Contains(field, StringComparer.Ordinal);

    /// <summary>The associated data that binds a value to its bridge, desired-printer entry and field.</summary>
    public static string AssociatedData(long bridgeId, long entryId, string field)
        => string.Create(CultureInfo.InvariantCulture, $"printlog-credential:{Version}:{bridgeId}:{entryId}:{field}");

    /// <summary>
    /// Parses a bridge's public key: standard padded base64 of the DER SPKI of an uncompressed
    /// P-256 point that is on the curve, with nothing before, after or inside it. Gives the
    /// fingerprint, lower-case hex SHA-256 of the SPKI bytes.
    /// </summary>
    public static bool TryParsePublicKey(string? value, out string? fingerprint)
    {
        fingerprint = null;
        if (value is null || !PublicKeyShape().IsMatch(value))
        {
            return false;
        }

        var spki = Convert.FromBase64String(value);
        if (spki.Length != SpkiPrefix.Length + 64 || !spki.AsSpan(0, SpkiPrefix.Length).SequenceEqual(SpkiPrefix)
            || Convert.ToBase64String(spki) != value || !OnCurve(spki.AsSpan(SpkiPrefix.Length, 64)))
        {
            return false;
        }

        fingerprint = Fingerprint(spki);
        return true;
    }

    public static string Fingerprint(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();

    public static bool IsFingerprint(string? value) => value is not null && FingerprintShape().IsMatch(value);

    /// <summary>
    /// Whether a value has the shape of a v1 sealed value: <c>v1.</c>, canonical unpadded
    /// base64url of 94 to 349 bytes, starting with an uncompressed P-256 point on the curve. Says
    /// nothing about whether it opens; only the bridge can tell.
    /// </summary>
    public static bool IsSealedValue(string? value)
    {
        if (value is null || value.Length > MaxSealedLength || !SealedShape().IsMatch(value))
        {
            return false;
        }

        var body = value[3..];
        if (body.Length % 4 == 1)
        {
            return false;
        }

        var bytes = Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/') + new string('=', (4 - body.Length % 4) % 4));
        if (bytes.Length is < MinSealedBytes or > MaxSealedBytes || bytes[0] != 0x04)
        {
            return false;
        }

        // Canonical: the unused bits of the last character are zero, so one value has one spelling.
        var canonical = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return canonical == body && OnCurve(bytes.AsSpan(1, 64));
    }

    /// <summary>
    /// Whether (x, y) is a point of P-256: both below p, and y² = x³ − 3x + b (mod p). The
    /// cofactor is 1, so that is the whole check.
    /// </summary>
    private static bool OnCurve(ReadOnlySpan<byte> xy)
    {
        var x = new BigInteger(xy[..32], isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(xy[32..], isUnsigned: true, isBigEndian: true);
        if (x >= P || y >= P)
        {
            return false;
        }

        var right = (BigInteger.ModPow(x, 3, P) - 3 * x + B) % P;
        if (right < 0)
        {
            right += P;
        }

        return BigInteger.ModPow(y, 2, P) == right;
    }

    private static BigInteger Unsigned(string hex) => new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);

    // \z, not $: $ also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z0-9+/]{122}==\z")]
    private static partial Regex PublicKeyShape();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex FingerprintShape();

    [GeneratedRegex(@"^v1\.[A-Za-z0-9_-]+\z")]
    private static partial Regex SealedShape();
}
