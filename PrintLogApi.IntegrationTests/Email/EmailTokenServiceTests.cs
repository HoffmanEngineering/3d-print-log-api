using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Analytics;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailTokenServiceTests
{
    private const string KeyA = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    private const string KeyB = "a2V5LWJrZXktYmtleS1ia2V5LWJrZXktYmtleS1ia2V5LWI=";
    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private static readonly DateTimeOffset Issued = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static (EmailTokenService Service, SettableTimeProvider Clock) Create(params string[] keys)
    {
        var clock = new SettableTimeProvider(Issued);
        var options = Options.Create(new EmailOptions { UnsubscribeSigningKeys = keys });
        return (new EmailTokenService(options, clock), clock);
    }

    [Fact]
    public void Unsubscribe_RoundTrips()
    {
        var (tokens, _) = Create(KeyA);

        var token = tokens.CreateUnsubscribe(123_456_789, EmailSettingTypes.MonthlyRecap);

        Assert.True(tokens.TryValidate(token, EmailTokenKind.Unsubscribe, out var payload));
        Assert.Equal(new EmailTokenPayload(123_456_789, EmailTokenKind.Unsubscribe, EmailSettingTypes.MonthlyRecap, Issued), payload);
    }

    [Fact]
    public void Manage_RoundTrips()
    {
        var (tokens, _) = Create(KeyA);

        Assert.True(tokens.TryValidate(tokens.CreateManage(42), EmailTokenKind.Manage, out var payload));
        Assert.Equal(42, payload.UserId);
        Assert.Equal(EmailTokenKind.Manage, payload.Kind);
    }

    [Fact]
    public void WrongKind_Fails()
    {
        var (tokens, _) = Create(KeyA);

        Assert.False(tokens.TryValidate(tokens.CreateUnsubscribe(1, EmailSettingTypes.All), EmailTokenKind.Manage, out _));
        Assert.False(tokens.TryValidate(tokens.CreateManage(1), EmailTokenKind.Unsubscribe, out _));
    }

    // Every character, replaced by every other character of the alphabet. This also catches
    // non-canonical base64url: the last character of a segment carries unused low bits, and a
    // decoder that ignores them would accept a different string as the same token.
    [Fact]
    public void AnySingleCharacterChange_Fails()
    {
        var (tokens, _) = Create(KeyA);
        var token = tokens.CreateUnsubscribe(987_654, EmailSettingTypes.PrinterSilent);

        for (var i = 0; i < token.Length; i++)
        {
            foreach (var c in Base64UrlAlphabet + ".")
            {
                if (c == token[i])
                {
                    continue;
                }

                var tampered = string.Concat(token.AsSpan(0, i), c.ToString(), token.AsSpan(i + 1));
                Assert.False(tokens.TryValidate(tampered, EmailTokenKind.Unsubscribe, out _), $"accepted {tampered}");
            }
        }
    }

    [Fact]
    public void RotatedKey_StillValidatesOldTokens()
    {
        var (oldTokens, _) = Create(KeyA);
        var (rotated, _) = Create(KeyB, KeyA);

        Assert.True(rotated.TryValidate(oldTokens.CreateManage(5), EmailTokenKind.Manage, out _));
    }

    [Fact]
    public void RetiredKey_Fails()
    {
        var (oldTokens, _) = Create(KeyA);
        var (retired, _) = Create(KeyB);

        Assert.False(retired.TryValidate(oldTokens.CreateManage(5), EmailTokenKind.Manage, out _));
    }

    [Fact]
    public void SignsWithTheFirstKey()
    {
        var (rotated, _) = Create(KeyB, KeyA);
        var (onlyB, _) = Create(KeyB);

        Assert.True(onlyB.TryValidate(rotated.CreateManage(5), EmailTokenKind.Manage, out _));
    }

    [Fact]
    public void ManageToken_ExpiresAfterLifetime()
    {
        var (tokens, clock) = Create(KeyA);
        var token = tokens.CreateManage(7);

        clock.SetUtcNow(Issued.AddDays(59));
        Assert.True(tokens.TryValidate(token, EmailTokenKind.Manage, out _));

        clock.SetUtcNow(Issued.AddDays(61));
        Assert.False(tokens.TryValidate(token, EmailTokenKind.Manage, out _));
    }

    [Fact]
    public void UnsubscribeToken_NeverExpires()
    {
        var (tokens, clock) = Create(KeyA);
        var token = tokens.CreateUnsubscribe(7, EmailSettingTypes.All);

        clock.SetUtcNow(Issued.AddYears(5));

        Assert.True(tokens.TryValidate(token, EmailTokenKind.Unsubscribe, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodot")]
    [InlineData(".")]
    [InlineData("a.b.c")]
    [InlineData("!!!!.????")]
    public void Malformed_Fails(string? token)
    {
        var (tokens, _) = Create(KeyA);
        Assert.False(tokens.TryValidate(token, EmailTokenKind.Unsubscribe, out _));
    }

    [Fact]
    public void Tokens_AreUrlSafe()
    {
        var (tokens, _) = Create(KeyA);

        Assert.Matches("^[A-Za-z0-9_\\-.]+$", tokens.CreateUnsubscribe(long.MaxValue, EmailSettingTypes.All));
        Assert.Matches("^[A-Za-z0-9_\\-.]+$", tokens.CreateManage(1));
    }
}
