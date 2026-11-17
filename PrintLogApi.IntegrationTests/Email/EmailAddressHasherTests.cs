using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailAddressHasherTests
{
    private static EmailAddressHasher Hasher(params string[] peppers)
        => new(Options.Create(new EmailOptions { SuppressionPeppers = peppers }));

    private const string PepperA = "cGVwcGVyLWEtcGVwcGVyLWEtcGVwcGVyLWEtcGVwcGVyLWE=";
    private const string PepperB = "cGVwcGVyLWItcGVwcGVyLWItcGVwcGVyLWItcGVwcGVyLWI=";

    [Fact]
    public void Normalize_TrimsAndLowercases() => Assert.Equal("a@b.com", EmailAddressHasher.Normalize("  A@B.Com "));

    [Fact]
    public void Hash_IgnoresCaseAndWhitespace()
        => Assert.Equal(Hasher(PepperA).Hash("a@b.com"), Hasher(PepperA).Hash(" A@B.com "));

    [Fact]
    public void Hash_IsLowercaseHex64()
        => Assert.Matches("^[0-9a-f]{64}$", Hasher(PepperA).Hash("a@b.com"));

    [Fact]
    public void Hash_DependsOnPepper()
        => Assert.NotEqual(Hasher(PepperA).Hash("a@b.com"), Hasher(PepperB).Hash("a@b.com"));

    [Fact]
    public void CandidateHashes_OnePerPepper_CurrentFirst()
    {
        var hasher = Hasher(PepperB, PepperA);
        var candidates = hasher.CandidateHashes("a@b.com");
        Assert.Equal([Hasher(PepperB).Hash("a@b.com"), Hasher(PepperA).Hash("a@b.com")], candidates);
    }
}
