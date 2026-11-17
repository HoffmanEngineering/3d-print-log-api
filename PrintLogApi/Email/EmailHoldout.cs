using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PrintLogApi.Email;

/// <summary>
/// Deterministic holdout assignment. SHA-256 rather than <c>GetHashCode</c>, which is randomized per
/// process: a user must stay in the same arm across restarts and releases for the comparison to mean
/// anything. The "v1" salt is part of the experiment's identity — changing it reshuffles everyone.
/// </summary>
public static class EmailHoldout
{
    public static bool IsHoldout(long userId, int percent)
    {
        if (percent <= 0) return false;
        if (percent >= 100) return true;

        var input = Encoding.UTF8.GetBytes("email-holdout-v1:" + userId.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(input);
        return BinaryPrimitives.ReadUInt32BigEndian(hash) % 100 < percent;
    }
}
