using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PrintLogApi.Email;

public interface IEmailAddressHasher
{
    /// <summary>Keyed hash of the normalized address with the current pepper.</summary>
    string Hash(string email);

    /// <summary>One hash per configured pepper, current first, for lookups that must survive a pepper rotation.</summary>
    IReadOnlyList<string> CandidateHashes(string email);
}

/// <summary>
/// HMAC-SHA-256 with a pepper held outside the database. A bare SHA-256 of an email address is
/// trivially enumerable, so it would not protect the address at all; the result is still
/// pseudonymous personal data, not anonymous.
/// </summary>
public sealed class EmailAddressHasher(IOptions<EmailOptions> options) : IEmailAddressHasher
{
    private readonly byte[][] _peppers = options.Value.SuppressionPeppers
        .Select(p => EmailOptionsValidator.TryDecode(p, out var bytes) ? bytes : Encoding.UTF8.GetBytes(p))
        .ToArray();

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public string Hash(string email)
    {
        if (_peppers.Length == 0)
        {
            throw new InvalidOperationException("Email:SuppressionPeppers is not configured.");
        }

        return HashWith(_peppers[0], email);
    }

    public IReadOnlyList<string> CandidateHashes(string email) => _peppers.Select(p => HashWith(p, email)).ToArray();

    private static string HashWith(byte[] pepper, string email)
        => Convert.ToHexStringLower(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(Normalize(email))));
}
