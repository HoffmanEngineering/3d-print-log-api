using System.Security.Cryptography;
using System.Text.Json;
using PrintLogApi.Services.Bridges;
using Xunit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// The <c>printlog-credential</c> v1 scheme (docs/bridge-credential-sealing.md) against its own
/// test vectors, which the bridge and the web app copy: the vectors open, reseal byte for byte,
/// refuse what must not open, and the API's strict parsers accept exactly the right shapes.
/// </summary>
public class BridgeCredentialSealingTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Bridges", "bridge-credential-sealing.vectors.json"))).RootElement.Clone();

    private static JsonElement Recipient => Vectors.GetProperty("recipient");

    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static ECDiffieHellman BridgeKey()
    {
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(S(Recipient, "privateKeyPkcs8Base64")), out _);
        return key;
    }

    public static TheoryData<int> VectorIndexes() => [.. Enumerable.Range(0, Vectors.GetProperty("vectors").GetArrayLength())];

    public static TheoryData<int> MustNotOpenIndexes() => [.. Enumerable.Range(0, Vectors.GetProperty("mustNotOpen").GetArrayLength())];

    public static TheoryData<string, string> InvalidPublicKeys()
    {
        var data = new TheoryData<string, string>();
        foreach (var bad in Vectors.GetProperty("invalidPublicKeys").EnumerateArray())
        {
            data.Add(S(bad, "why"), S(bad, "value"));
        }

        return data;
    }

    public static TheoryData<string, string> InvalidSealedValues()
    {
        var data = new TheoryData<string, string>();
        foreach (var bad in Vectors.GetProperty("invalidSealedValues").EnumerateArray())
        {
            data.Add(S(bad, "why"), S(bad, "value"));
        }

        return data;
    }

    [Fact]
    public void TheRecipientKey_IsAcceptedAndItsFingerprintMatches()
    {
        Assert.True(SealedCredentials.TryParsePublicKey(S(Recipient, "publicKey"), out var fingerprint));
        Assert.Equal(S(Recipient, "fingerprint"), fingerprint);

        using var key = BridgeKey();
        Assert.Equal(S(Recipient, "publicKeyRawHex"), Convert.ToHexString(CredentialSealer.Raw(key)).ToLowerInvariant());
        Assert.Equal(S(Recipient, "publicKey"), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void EachVector_OpensWithTheBridgeKey(int index)
    {
        var v = Vectors.GetProperty("vectors")[index];
        var ad = CredentialSealer.AssociatedData(v.GetProperty("bridgeId").GetInt64(), v.GetProperty("entryId").GetInt64(), S(v, "field"));
        Assert.Equal(S(v, "associatedData"), ad);
        Assert.Equal(S(v, "associatedData"), SealedCredentials.AssociatedData(v.GetProperty("bridgeId").GetInt64(), v.GetProperty("entryId").GetInt64(), S(v, "field")));

        using var key = BridgeKey();
        Assert.Equal(S(v, "plaintext"), CredentialSealer.Open(key, S(v, "sealed"), ad));
        Assert.Equal(S(v, "plaintextUtf8Hex"), Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(S(v, "plaintext"))).ToLowerInvariant());
        Assert.True(SealedCredentials.IsSealedValue(S(v, "sealed")));
    }

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void EachVector_ResealsByteForByte_FromItsEphemeralKeyAndNonce(int index)
    {
        var v = Vectors.GetProperty("vectors")[index];
        using var ephemeral = CredentialSealer.FromHex(S(v, "ephemeralPrivateKeyHex"), S(v, "ephemeralPublicKeyHex"));

        var resealed = CredentialSealer.Seal(S(Recipient, "publicKey"), S(v, "associatedData"), S(v, "plaintext"), ephemeral, Convert.FromHexString(S(v, "nonceHex")));

        Assert.Equal(S(v, "sealed"), resealed);
    }

    [Theory]
    [MemberData(nameof(VectorIndexes))]
    public void AFreshSeal_RoundTrips_AndNeverRepeats(int index)
    {
        var v = Vectors.GetProperty("vectors")[index];
        var first = CredentialSealer.Seal(S(Recipient, "publicKey"), S(v, "associatedData"), S(v, "plaintext"));
        var second = CredentialSealer.Seal(S(Recipient, "publicKey"), S(v, "associatedData"), S(v, "plaintext"));

        using var key = BridgeKey();
        Assert.Equal(S(v, "plaintext"), CredentialSealer.Open(key, first, S(v, "associatedData")));
        Assert.NotEqual(first, second);
        Assert.True(SealedCredentials.IsSealedValue(first));
    }

    [Theory]
    [MemberData(nameof(MustNotOpenIndexes))]
    public void WhatMustNotOpen_DoesNotOpen_ThoughItsShapeIsValid(int index)
    {
        var n = Vectors.GetProperty("mustNotOpen")[index];
        using var key = BridgeKey();

        Assert.ThrowsAny<CryptographicException>(() => CredentialSealer.Open(key, S(n, "sealed"), S(n, "associatedData")));
        // The API cannot tell: it checks the shape only.
        Assert.True(SealedCredentials.IsSealedValue(S(n, "sealed")));
    }

    [Theory]
    [MemberData(nameof(InvalidPublicKeys))]
    public void AnInvalidPublicKey_IsRefused(string why, string value)
    {
        Assert.False(SealedCredentials.TryParsePublicKey(value, out var fingerprint), why);
        Assert.Null(fingerprint);
    }

    [Theory]
    [MemberData(nameof(InvalidSealedValues))]
    public void AnInvalidSealedValue_IsRefused(string why, string value)
        => Assert.False(SealedCredentials.IsSealedValue(value), why);

    [Fact]
    public void ANullOrOverlongValue_IsRefused()
    {
        Assert.False(SealedCredentials.TryParsePublicKey(null, out _));
        Assert.False(SealedCredentials.IsSealedValue(null));
        Assert.False(SealedCredentials.IsSealedValue("v1." + new string('A', 10_000)));
    }

    [Fact]
    public void AFreshlyGeneratedKey_IsAccepted_AsEveryClientExportsIt()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();

        Assert.True(SealedCredentials.TryParsePublicKey(Convert.ToBase64String(spki), out var fingerprint));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(), fingerprint);
    }

    [Theory]
    [InlineData("6912dab083628b441666abe23f4b8c81bfea54f84e9e350abccbbfc83f9c8954", true)]
    [InlineData("6912DAB083628B441666ABE23F4B8C81BFEA54F84E9E350ABCCBBFC83F9C8954", false)]
    [InlineData("6912dab083628b44", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Fingerprints_AreSixtyFourLowerCaseHex(string? value, bool valid)
        => Assert.Equal(valid, SealedCredentials.IsFingerprint(value));

    [Theory]
    [InlineData("bambu", new[] { "access_code" })]
    [InlineData("prusalink", new[] { "password", "username", "api_key" })]
    [InlineData("moonraker", new[] { "moonraker_api_key" })]
    [InlineData("octoprint", new[] { "api_key" })]
    [InlineData("sdcp", new string[0])]
    public void EachKind_TakesItsAdaptersFields(string kind, string[] fields)
        => Assert.Equal(fields, SealedCredentials.FieldsFor(kind));
}
