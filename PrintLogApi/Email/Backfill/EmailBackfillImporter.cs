using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.Email.Backfill;

/// <param name="Read">Lines in the export.</param>
/// <param name="Matched">Lines whose Auth0 user id belongs to a 3D Print Log user.</param>
/// <param name="Updated">Matched users whose row changed (or would change, on a dry run).</param>
/// <param name="Unmatched">Auth0 accounts that never signed in to 3D Print Log.</param>
/// <param name="Verified">Matched users with a verified address.</param>
/// <param name="Unverified">Matched users with an address Auth0 has not verified.</param>
public record EmailBackfillResult(int Read, int Matched, int Updated, int Unmatched, int Verified, int Unverified);

/// <summary>
/// Copies addresses from an Auth0 export onto Users, for accounts that have not signed in since
/// the email claims Action was deployed (live sign-ins are synced by the claims transformer).
/// Runs offline from PrintLogApi.Tools against the production connection string.
/// </summary>
public sealed class EmailBackfillImporter(PrintLogContext db, TimeProvider clock)
{
    public const int BatchSize = 500;

    public async Task<EmailBackfillResult> ImportAsync(
        IAsyncEnumerable<Auth0ExportUser> users, bool dryRun, CancellationToken ct)
    {
        var totals = new EmailBackfillResult(0, 0, 0, 0, 0, 0);
        var batch = new List<Auth0ExportUser>(BatchSize);

        await foreach (var user in users.WithCancellation(ct))
        {
            batch.Add(user);
            if (batch.Count == BatchSize)
            {
                totals = Add(totals, await ImportBatchAsync(batch, dryRun, ct));
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            totals = Add(totals, await ImportBatchAsync(batch, dryRun, ct));
        }

        return totals;
    }

    private async Task<EmailBackfillResult> ImportBatchAsync(List<Auth0ExportUser> batch, bool dryRun, CancellationToken ct)
    {
        var ids = batch.Select(u => u.UserId).ToList();
        var rows = await db.Users
            .Where(u => u.OAuthUserId != null && ids.Contains(u.OAuthUserId))
            .ToDictionaryAsync(u => u.OAuthUserId!, ct);

        var now = clock.GetUtcNow();
        int matched = 0, updated = 0, verified = 0, unverified = 0;

        foreach (var export in batch)
        {
            if (!rows.TryGetValue(export.UserId, out var row))
            {
                continue;
            }

            matched++;
            var changed = false;

            // Trimmed but not lower-cased: the local part of an address is case-sensitive, and
            // suppression matching normalizes separately (EmailAddressHasher).
            var email = export.Email?.Trim();
            if (!string.IsNullOrEmpty(email))
            {
                if (export.EmailVerified)
                {
                    verified++;
                }
                else
                {
                    unverified++;
                }

                if (row.Email != email || row.EmailVerified != export.EmailVerified)
                {
                    row.Email = email;
                    row.EmailVerified = export.EmailVerified;
                    row.EmailUpdatedAt = now;
                    changed = true;
                }
            }

            // Never overwrite: a signup date the API recorded is the one the onboarding series
            // keys on, and an export cannot know better.
            if (row.CreatedDate is null && export.CreatedAt is not null)
            {
                row.CreatedDate = export.CreatedAt;
                changed = true;
            }

            if (changed)
            {
                updated++;
            }
        }

        if (!dryRun)
        {
            await db.SaveChangesAsync(ct);
        }

        db.ChangeTracker.Clear();
        return new EmailBackfillResult(batch.Count, matched, updated, batch.Count - matched, verified, unverified);
    }

    private static EmailBackfillResult Add(EmailBackfillResult a, EmailBackfillResult b) => new(
        a.Read + b.Read, a.Matched + b.Matched, a.Updated + b.Updated,
        a.Unmatched + b.Unmatched, a.Verified + b.Verified, a.Unverified + b.Unverified);
}
