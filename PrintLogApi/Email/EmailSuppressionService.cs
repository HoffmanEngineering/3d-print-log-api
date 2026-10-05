using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;

namespace PrintLogApi.Email;

public interface IEmailSuppressionService
{
    /// <summary>Adds the address (or an already-computed hash) to the suppression list. Idempotent.</summary>
    Task SuppressAsync(string emailOrHash, bool isHash, EmailSuppressionReason reason, string? detail, CancellationToken ct);

    /// <summary>True when the address matches a suppression under any configured pepper.</summary>
    Task<bool> IsSuppressedAsync(string email, CancellationToken ct);
}

/// <summary>
/// The do-not-send list. Keyed by a peppered hash of the address with no link to an account, so
/// it outlives account deletion: someone who complained stays suppressed if they sign up again.
/// </summary>
public sealed class EmailSuppressionService(PrintLogContext db, IEmailAddressHasher hasher, TimeProvider clock) : IEmailSuppressionService
{
    private const int MaxDetailLength = 256;

    public async Task SuppressAsync(string emailOrHash, bool isHash, EmailSuppressionReason reason, string? detail, CancellationToken ct)
    {
        IReadOnlyList<string> candidates = isHash ? [emailOrHash] : hasher.CandidateHashes(emailOrHash);
        if (await db.EmailSuppressions.AnyAsync(s => candidates.Contains(s.EmailHash), ct))
        {
            return;
        }

        var row = new EmailSuppression
        {
            EmailHash = isHash ? emailOrHash : hasher.Hash(emailOrHash),
            Reason = reason,
            CreatedAt = clock.GetUtcNow(),
            Detail = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail,
        };
        db.EmailSuppressions.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            // A concurrent delivery of the same event got there first: the outcome we wanted.
            db.Entry(row).State = EntityState.Detached;
        }
    }

    public async Task<bool> IsSuppressedAsync(string email, CancellationToken ct)
    {
        var candidates = hasher.CandidateHashes(email);
        return await db.EmailSuppressions.AnyAsync(s => candidates.Contains(s.EmailHash), ct);
    }
}
