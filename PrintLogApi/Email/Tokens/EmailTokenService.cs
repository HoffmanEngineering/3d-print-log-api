using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Email.Tokens;

public enum EmailTokenKind : byte
{
    /// <summary>Turns one category (or all email) off. Never expires; grants nothing else.</summary>
    Unsubscribe = 1,

    /// <summary>Reads and writes the user's email preferences. Expires after <see cref="EmailOptions.ManageTokenLifetime"/>.</summary>
    Manage = 2,
}

/// <param name="Category">The <see cref="EmailSettingTypes"/> id an unsubscribe token switches off (22 = all); 0 on a manage token.</param>
public record EmailTokenPayload(long UserId, EmailTokenKind Kind, int Category, DateTimeOffset IssuedAt);

public interface IEmailTokenService
{
    string CreateUnsubscribe(long userId, int category);

    string CreateManage(long userId);

    /// <summary>
    /// False for anything malformed, signed by no configured key, of the other kind, or a manage
    /// token older than its lifetime.
    /// </summary>
    bool TryValidate(string? token, EmailTokenKind expected, out EmailTokenPayload payload);
}

/// <summary>
/// Signed, stateless tokens for the links in an email (spec §6.7). Layout:
/// <c>version | kind | userId (LEB128) | category | issuedAt (uint32 BE, unix minutes)</c>, then
/// <c>base64url(payload) + "." + base64url(HMAC-SHA256(key, payload)[..16])</c>.
/// Signed with the first configured key, verified against any, so keys rotate by prepending.
/// </summary>
public sealed class EmailTokenService : IEmailTokenService
{
    private const byte Version = 1;
    private const int MacBytes = 16;
    private const int MaxVarintBytes = 10;

    private static readonly SearchValues<char> Base64UrlAlphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private readonly byte[][] _keys;
    private readonly TimeSpan _manageLifetime;
    private readonly TimeProvider _clock;

    public EmailTokenService(IOptions<EmailOptions> options, TimeProvider clock)
    {
        _keys = options.Value.UnsubscribeSigningKeys
            .Select(k => EmailOptionsValidator.TryDecode(k, out var bytes) ? bytes : null)
            .OfType<byte[]>()
            .ToArray();
        _manageLifetime = options.Value.ManageTokenLifetime;
        _clock = clock;
    }

    public string CreateUnsubscribe(long userId, int category)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(category);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(category, byte.MaxValue);
        return Create(userId, EmailTokenKind.Unsubscribe, (byte)category);
    }

    public string CreateManage(long userId) => Create(userId, EmailTokenKind.Manage, 0);

    private string Create(long userId, EmailTokenKind kind, byte category)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userId);
        if (_keys.Length == 0)
        {
            throw new InvalidOperationException("Email:UnsubscribeSigningKeys is empty; tokens cannot be signed.");
        }

        var payload = new byte[2 + MaxVarintBytes + 1 + 4];
        payload[0] = Version;
        payload[1] = (byte)kind;
        var length = 2;

        var value = (ulong)userId;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            payload[length++] = value == 0 ? b : (byte)(b | 0x80);
        }
        while (value != 0);

        payload[length++] = category;
        var minutes = (uint)(_clock.GetUtcNow().ToUnixTimeSeconds() / 60);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(length, 4), minutes);
        length += 4;

        var body = payload.AsSpan(0, length);
        return Base64Url.EncodeToString(body) + "." + Base64Url.EncodeToString(Mac(_keys[0], body));
    }

    public bool TryValidate(string? token, EmailTokenKind expected, out EmailTokenPayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(token) || _keys.Length == 0)
        {
            return false;
        }

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot != token.LastIndexOf('.') || dot == token.Length - 1)
        {
            return false;
        }

        if (!TryDecodeCanonical(token.AsSpan(0, dot), out var body)
            || !TryDecodeCanonical(token.AsSpan(dot + 1), out var mac)
            || mac.Length != MacBytes)
        {
            return false;
        }

        // Authenticate before interpreting a single payload byte.
        var authentic = false;
        foreach (var key in _keys)
        {
            authentic |= CryptographicOperations.FixedTimeEquals(Mac(key, body), mac);
        }

        if (!authentic || !TryParse(body, out payload) || payload.Kind != expected)
        {
            return false;
        }

        return expected != EmailTokenKind.Manage || _clock.GetUtcNow() - payload.IssuedAt <= _manageLifetime;
    }

    private static byte[] Mac(byte[] key, ReadOnlySpan<byte> body) => HMACSHA256.HashData(key, body)[..MacBytes];

    /// <summary>
    /// Decodes base64url and rejects any spelling other than the one the encoder produces. The
    /// final character of a segment carries unused low bits, so a lenient decoder accepts
    /// several strings as the same bytes; a token must have exactly one valid form.
    /// </summary>
    private static bool TryDecodeCanonical(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];

        // TryDecodeFromChars throws, rather than returning false, on a character outside the
        // alphabet or a length no encoder produces (n % 4 == 1). These tokens arrive from
        // anonymous callers, so both must be a plain reject, never a 500.
        if (text.ContainsAnyExcept(Base64UrlAlphabet) || text.Length % 4 == 1)
        {
            return false;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(text.Length)];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(text, buffer, out written))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        bytes = buffer[..written];
        return Base64Url.EncodeToString(bytes).AsSpan().SequenceEqual(text);
    }

    private static bool TryParse(ReadOnlySpan<byte> body, out EmailTokenPayload payload)
    {
        payload = default!;
        if (body.Length < 2 || body[0] != Version || body[1] is not ((byte)EmailTokenKind.Unsubscribe or (byte)EmailTokenKind.Manage))
        {
            return false;
        }

        var kind = (EmailTokenKind)body[1];
        var position = 2;
        ulong userId = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (position >= body.Length || shift >= 63)
            {
                return false;
            }

            var b = body[position++];
            userId |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        if (userId > long.MaxValue || body.Length - position != 1 + 4)
        {
            return false;
        }

        var category = body[position++];
        var minutes = BinaryPrimitives.ReadUInt32BigEndian(body[position..]);
        payload = new EmailTokenPayload((long)userId, kind, category, DateTimeOffset.FromUnixTimeSeconds(minutes * 60L));
        return true;
    }
}
