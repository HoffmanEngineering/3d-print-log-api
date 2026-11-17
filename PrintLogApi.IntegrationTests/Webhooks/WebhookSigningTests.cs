using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PrintLogApi.Webhooks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>The signature header and the secret at rest (#235).</summary>
public class WebhookSigningTests
{
    private static readonly string KeyA = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook-secret-key-A-0123456789abcdef"));
    private static readonly string KeyB = Convert.ToBase64String(Encoding.UTF8.GetBytes("webhook-secret-key-B-0123456789abcdef"));

    private static WebhookSecretProtector Protector(params string[] keys) =>
        new(Options.Create(new WebhookOptions { SecretEncryptionKeys = keys }));

    [Fact]
    public void Signature_IsStripeStyle_HmacSha256OverTimestampDotBody()
    {
        // Vector computed with: printf '1760616000.{"a":1}' | openssl dgst -sha256 -hmac 'whsec_test'
        var header = WebhookSignature.Create("whsec_test", 1760616000, Encoding.UTF8.GetBytes("{\"a\":1}"));

        Assert.Equal("t=1760616000,v1=d206854a9a99d9057473a7b4cc59c073271c159617be901d9b871e4e4b03ba4d", header);
    }

    [Fact]
    public void GeneratedSecrets_ArePrefixed_UrlSafe_AndUnique()
    {
        var a = WebhookSecretProtector.GenerateSecret();
        var b = WebhookSecretProtector.GenerateSecret();

        Assert.Matches("^whsec_[A-Za-z0-9_-]{43}$", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Protect_RoundTrips_AndDoesNotStoreThePlaintext()
    {
        var protector = Protector(KeyA);
        var id = Guid.NewGuid();
        var secret = WebhookSecretProtector.GenerateSecret();

        var stored = protector.Protect(id, secret);

        Assert.DoesNotContain(secret["whsec_".Length..], stored);
        Assert.StartsWith("v1.", stored);
        Assert.True(stored.Length <= 256, "fits the column");
        Assert.Equal(secret, protector.Unprotect(id, stored));
    }

    [Fact]
    public void TheKeyId_IsDerivedWithItsOwnHkdfInfo_NotAPlainHashOfTheKey()
    {
        var key = Convert.FromBase64String(KeyA);
        var expected = Convert.ToHexStringLower(HKDF.DeriveKey(
            HashAlgorithmName.SHA256, key, 4, salt: null, info: Encoding.UTF8.GetBytes("3dprintlog webhook key id v1")));

        var stored = Protector(KeyA).Protect(Guid.NewGuid(), "whsec_id");

        Assert.Equal(expected, stored.Split('.')[1]);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(key).AsSpan(0, 4)), stored.Split('.')[1]);
    }

    [Fact]
    public void Protect_UsesAFreshNonceEachTime()
    {
        var protector = Protector(KeyA);
        var id = Guid.NewGuid();

        Assert.NotEqual(protector.Protect(id, "whsec_same"), protector.Protect(id, "whsec_same"));
    }

    [Fact]
    public void Unprotect_FailsForAnotherSubscription()
    {
        var protector = Protector(KeyA);
        var stored = protector.Protect(Guid.NewGuid(), "whsec_moved");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), stored));
    }

    [Fact]
    public void Unprotect_FailsWhenTampered()
    {
        var protector = Protector(KeyA);
        var id = Guid.NewGuid();
        var stored = protector.Protect(id, "whsec_tamper");
        var bytes = Convert.FromBase64String(stored[(stored.LastIndexOf('.') + 1)..]);
        bytes[^1] ^= 1;
        var tampered = stored[..(stored.LastIndexOf('.') + 1)] + Convert.ToBase64String(bytes);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(id, tampered));
    }

    [Fact]
    public void Rotation_NewKeyFirst_StillDecryptsWithTheOldKey()
    {
        var id = Guid.NewGuid();
        var stored = Protector(KeyA).Protect(id, "whsec_old");

        var rotated = Protector(KeyB, KeyA);

        Assert.Equal("whsec_old", rotated.Unprotect(id, stored));
        Assert.Equal("whsec_new", Protector(KeyB).Unprotect(id, rotated.Protect(id, "whsec_new")));
    }

    [Fact]
    public void Unprotect_FailsWhenTheKeyIsGone()
    {
        var id = Guid.NewGuid();
        var stored = Protector(KeyA).Protect(id, "whsec_lost");

        Assert.ThrowsAny<CryptographicException>(() => Protector(KeyB).Unprotect(id, stored));
    }

    [Fact]
    public void WithoutKeys_ItIsNotConfigured()
    {
        Assert.False(Protector().IsConfigured);
        Assert.True(Protector(KeyA).IsConfigured);
        Assert.Throws<InvalidOperationException>(() => Protector().Protect(Guid.NewGuid(), "whsec_x"));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQ=")] // "short": under 32 bytes
    public void OptionsValidation_RejectsAMalformedKey(string key)
    {
        var result = new WebhookOptionsValidator().Validate(null, new WebhookOptions { SecretEncryptionKeys = [key] });

        Assert.True(result.Failed);
    }

    [Fact]
    public void OptionsValidation_AcceptsNoKeys_AndGoodKeys()
    {
        var validator = new WebhookOptionsValidator();

        Assert.True(validator.Validate(null, new WebhookOptions()).Succeeded);
        Assert.True(validator.Validate(null, new WebhookOptions { SecretEncryptionKeys = [KeyA, KeyB] }).Succeeded);
    }
}
