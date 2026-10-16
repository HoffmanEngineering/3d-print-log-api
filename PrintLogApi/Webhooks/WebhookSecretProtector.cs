using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Webhooks;

/// <summary>
/// Encrypts webhook signing secrets at rest (#235). A signing secret has to be recoverable, so it
/// cannot be hashed like an API key or a Spoolman secret.
/// </summary>
/// <remarks>
/// The repo has no ASP.NET Core data protection key ring, and the default App Service key-ring
/// location is not guaranteed for this deployment, so this follows <c>Email:UnsubscribeSigningKeys</c>:
/// keys from configuration, the first one current, every one accepted. Each key is stretched with
/// HKDF into an AES-256-GCM key. The subscription id is the associated data, so a ciphertext copied
/// onto another row does not decrypt. Stored as <c>v1.&lt;key id&gt;.&lt;base64(nonce|ciphertext|tag)&gt;</c>,
/// where the key id (the first 4 bytes of the key's SHA-256) picks the key without trying them all.
/// </remarks>
public sealed class WebhookSecretProtector
{
    public const string SecretPrefix = "whsec_";
    private const string Version = "v1";
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("3dprintlog webhook secret v1");

    private readonly (string Id, byte[] Key)[] _keys;

    public WebhookSecretProtector(IOptions<WebhookOptions> options)
    {
        _keys = options.Value.SecretEncryptionKeys
            .Select(k => WebhookOptionsValidator.TryDecode(k, out var bytes) ? bytes : null)
            .OfType<byte[]>()
            .Select(k => (Convert.ToHexStringLower(SHA256.HashData(k).AsSpan(0, 4)),
                HKDF.DeriveKey(HashAlgorithmName.SHA256, k, 32, salt: null, info: HkdfInfo)))
            .ToArray();
    }

    /// <summary>False when no key is configured: webhooks cannot be created or delivered.</summary>
    public bool IsConfigured => _keys.Length > 0;

    /// <summary>A new signing secret: <c>whsec_</c> and 32 random bytes, base64url.</summary>
    public static string GenerateSecret() =>
        SecretPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public string Protect(Guid subscriptionId, string secret)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Webhooks:SecretEncryptionKeys is empty; secrets cannot be stored.");
        }

        var (id, key) = _keys[0];
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var output = new byte[NonceBytes + plaintext.Length + TagBytes];
        var nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceBytes, plaintext.Length),
            output.AsSpan(NonceBytes + plaintext.Length), subscriptionId.ToByteArray());

        return $"{Version}.{id}.{Convert.ToBase64String(output)}";
    }

    /// <summary>
    /// The secret stored for <paramref name="subscriptionId"/>. Throws a
    /// <see cref="CryptographicException"/> when the value is malformed, was tampered with, belongs
    /// to another row, or its key is no longer configured.
    /// </summary>
    public string Unprotect(Guid subscriptionId, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || parts[0] != Version)
        {
            throw new CryptographicException("Unrecognized webhook secret format.");
        }

        var key = _keys.FirstOrDefault(k => k.Id == parts[1]).Key
            ?? throw new CryptographicException("The key that encrypted this webhook secret is not configured.");

        byte[] data;
        try
        {
            data = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Unrecognized webhook secret format.", ex);
        }
        if (data.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Unrecognized webhook secret format.");
        }

        var cipherLength = data.Length - NonceBytes - TagBytes;
        var plaintext = new byte[cipherLength];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(data.AsSpan(0, NonceBytes), data.AsSpan(NonceBytes, cipherLength),
            data.AsSpan(NonceBytes + cipherLength), plaintext, subscriptionId.ToByteArray());
        return Encoding.UTF8.GetString(plaintext);
    }
}
