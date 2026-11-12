using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Services.Spoolman;

namespace PrintLogApi.Services;

public class UserDeletionService : IUserDeletionService
{
    private readonly PrintLogContext _context;
    private readonly ILogger<UserDeletionService> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly IAuth0Service _auth0Service;
    private readonly IBlobStorageService _blobStorageService;
    private readonly ICacheVersionService _cacheVersionService;
    private readonly ISpoolmanCredentialCache _spoolmanCredentials;
    private readonly TimeProvider _timeProvider;
    private readonly int _deactivationTimeInMinutes;

    public UserDeletionService(PrintLogContext context,
                               ILogger<UserDeletionService> logger,
                               TelemetryClient telemetry,
                               IConfiguration config,
                               IAuth0Service auth0Service,
                               IBlobStorageService blobStorageService,
                               ICacheVersionService cacheVersionService,
                               ISpoolmanCredentialCache spoolmanCredentials,
                               TimeProvider? timeProvider = null)
    {
        _context = context;
        _logger = logger;
        _telemetry = telemetry;
        _auth0Service = auth0Service;
        _blobStorageService = blobStorageService;
        _cacheVersionService = cacheVersionService;
        _spoolmanCredentials = spoolmanCredentials;
        // Optional so the tests that construct this service by hand need not pass one; DI
        // supplies the registered TimeProvider.System.
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Null-forgiven: an absent PendingUserDeactivationTimeInMinutes is a deployment
        // misconfiguration that already threw here, at service construction.
        _deactivationTimeInMinutes = int.Parse(config["PendingUserDeactivationTimeInMinutes"]!, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Auth0 first, then the database.</b> The other order orphaned the Auth0 account whenever
    /// Auth0 failed after the rows were gone: with no user row left, no later run could find it
    /// again. In this order the user row is the last thing deleted, so every partial failure leaves
    /// it in place and the next run repeats the whole user. Auth0 failing leaves every row; a
    /// database step failing after Auth0 succeeded is finished next run, whose Auth0 delete of the
    /// account that is already gone succeeds (<see cref="Auth0Service.DeleteUser"/> treats 404 as
    /// done). The database steps are each idempotent: deletes by owner, and a blob already gone
    /// is logged and skipped. The Stripe cancel is the first of them, so a user whose cancel
    /// failed has lost only the Auth0 account (they can no longer sign in) and keeps every row.
    /// </para>
    /// <para>
    /// <b>One user's failure never stops the others.</b> It is logged with the user id, counted in
    /// <c>DeletePendingDeactivatedUserFailed</c>, and the loop goes on. Before this, one exception
    /// aborted the batch, and as every run read the same users first, every later run too.
    /// </para>
    /// </remarks>
    public async Task<PendingUserDeletionResult> DeletePendingDeactivatedUsers(CancellationToken ct = default)
    {
        // An instant compared with DeactivationDateTime, never stored, so the offset it carries
        // (UTC here, server local before) does not change which rows match.
        var pendingDeactivationTime = _timeProvider.GetUtcNow().AddMinutes(-_deactivationTimeInMinutes);

        // Find users whose deactivation date is before the pending deactivation time. Oldest
        // account first, so the order is stable from run to run.
        var usersToDelete = await _context.Users
            .Where(u => u.DeactivationDateTime != null && u.DeactivationDateTime <= pendingDeactivationTime)
            .OrderBy(u => u.Id)
            .ToListAsync(ct);

        _logger.LogInformation("Deleting {count} deactivated users before {deactivationDate}", usersToDelete.Count, pendingDeactivationTime.ToString(CultureInfo.InvariantCulture));

        var deleted = 0;
        var failed = 0;
        foreach (var user in usersToDelete)
        {
            ct.ThrowIfCancellationRequested();

            var internalId = user.Id;
            var oauthUserId = user.OAuthUserId;
            _logger.LogInformation("Deleting User {id} with oauth id {oauthId}", internalId, oauthUserId);

            var stage = "Auth0";
            try
            {
                // Delete the user from the Auth Server first; see the remarks for why.
                if (string.IsNullOrWhiteSpace(oauthUserId))
                {
                    _logger.LogWarning("User {id} has no oauth id; deleting their data without an Auth0 call", internalId);
                }
                else
                {
                    await _auth0Service.DeleteUser(oauthUserId);
                }

                // Delete all data from the database, the user row last.
                stage = "Database";
                await DeleteAllDataForUser(user);
                deleted++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                _logger.LogError(ex, "Deleting pending deactivated user {id} failed at the {stage} step; it is retried on the next run", internalId, stage);
                _telemetry.TrackEvent("DeletePendingDeactivatedUserFailed", new Dictionary<string, string>
                {
                    ["UserId"] = internalId.ToString(CultureInfo.InvariantCulture),
                    ["Stage"] = stage,
                    ["ExceptionType"] = ex.GetType().FullName ?? ex.GetType().Name,
                });

                // A failed SaveChanges leaves this user's removal tracked, and the next user's
                // SaveChanges would send it again and fail too. Every other step bypasses the
                // tracker (ExecuteDelete/ExecuteUpdate), so clearing it loses nothing.
                _context.ChangeTracker.Clear();
            }
        }

        if (usersToDelete.Count > 0)
        {
            _telemetry.TrackEvent("DeletePendingDeactivatedUsers", new Dictionary<string, string>()
            {
                { "Count", usersToDelete.Count.ToString(CultureInfo.InvariantCulture) },
                { "Deleted", deleted.ToString(CultureInfo.InvariantCulture) },
                { "Failed", failed.ToString(CultureInfo.InvariantCulture) },
            });
        }

        return new PendingUserDeletionResult(usersToDelete.Count, deleted, failed);
    }

    /// <summary>
    /// Deletes the user's device grants, and with them (a database cascade) their bridges. A
    /// connection's link to a bridge has no cascade (SQL Server 1785), so the links are cleared in
    /// the same transaction; a heartbeat that relinks one in between fails the delete, which is
    /// then retried, the same as a single revoke (DeviceGrantService).
    /// </summary>
    private async Task DeleteDeviceGrantsAsync(long userId)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var strategy = _context.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync();
                    await _context.Connections
                        .Where(c => c.UserId == userId && c.BridgeId != null)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.BridgeId, (long?)null));
                    await _context.DeviceGrants
                        .Where(g => g.UserId == userId)
                        .ExecuteDeleteAsync();
                    await transaction.CommitAsync();
                });
                return;
            }
            catch (System.Data.Common.DbException) when (attempt < attempts)
            {
            }
        }
    }

    public async Task DeleteAllDataForUser(User user)
    {
        if (user is null)
        {
            throw new ArgumentNullException(nameof(user));
        }

        long userId = user.Id;

        // Increase command timeout for this complex operation
        var previousTimeout = _context.Database.GetCommandTimeout();
        _context.Database.SetCommandTimeout(300); // 5 minutes

        try
        {
            // Before any row goes: a cancel that fails throws here, and the user is retried with
            // every row, the subscription's included, still in place.
            await CancelStripeSubscriptionsAsync(userId);

            // Get print IDs for this user (needed for cascading deletes)
            var printIds = await _context.Prints
                .Where(p => p.CreatedById == userId)
                .Select(p => p.Id)
                .ToListAsync();

            // Get comment IDs created by this user
            var commentIds = await _context.Comments
                .Where(c => c.CreatedById == userId)
                .Select(c => c.Id)
                .ToListAsync();

            // Delete in order to respect foreign key constraints
            // Child tables first, then parent tables

            // Delete notifications owned by this user or linked to records that are being removed.
            await _context.Notifications
                .Where(n => n.UserId == userId
                    || (n.PrintId.HasValue && printIds.Contains(n.PrintId.Value))
                    || (n.CommentId.HasValue && commentIds.Contains(n.CommentId.Value)))
                .ExecuteDeleteAsync();

            // Delete PrintComments for user's prints
            if (printIds.Count > 0)
            {
                await _context.PrintComments
                    .Where(pc => printIds.Contains(pc.PrintId))
                    .ExecuteDeleteAsync();
            }

            // Delete PrintComments for user's comments on other prints
            if (commentIds.Count > 0)
            {
                await _context.PrintComments
                    .Where(pc => commentIds.Contains(pc.CommentId))
                    .ExecuteDeleteAsync();
            }

            // Delete Comments created by user
            await _context.Comments
                .Where(c => c.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete PrintImages, their blobs, and their associated Files for user's prints
            if (printIds.Count > 0)
            {
                // The thumbnail and social derivatives are Restrict FKs, so the image rows must go
                // before their File rows, and their blobs must be listed here or nothing deletes them.
                var printImageData = await _context.PrintImages
                    .Where(pi => printIds.Contains(pi.PrintId))
                    .Select(pi => new
                    {
                        pi.FileId,
                        pi.ThumbnailFileId,
                        pi.SocialFileId,
                        pi.File.Path,
                        ThumbnailPath = pi.ThumbnailFile != null ? pi.ThumbnailFile.Path : null,
                        SocialPath = pi.SocialFile != null ? pi.SocialFile.Path : null,
                    })
                    .AsNoTracking()
                    .ToListAsync();

                await DeleteImageBlobsAsync(
                    BlobContainers.PrintImages,
                    printImageData.SelectMany(f => new[] { f.Path, f.ThumbnailPath, f.SocialPath }));

                await _context.PrintImages
                    .Where(pi => printIds.Contains(pi.PrintId))
                    .ExecuteDeleteAsync();

                var printImageFileIds = printImageData
                    .SelectMany(f => new[] { (Guid?)f.FileId, f.ThumbnailFileId, f.SocialFileId })
                    .OfType<Guid>()
                    .ToList();
                if (printImageFileIds.Count > 0)
                {
                    await _context.Files
                        .Where(f => printImageFileIds.Contains(f.Id))
                        .ExecuteDeleteAsync();
                }
            }

            // Delete PrintFilament for user's prints
            if (printIds.Count > 0)
            {
                await _context.PrintFilament
                    .Where(pf => printIds.Contains(pf.PrintId))
                    .ExecuteDeleteAsync();
            }

            // Delete PrintAttachments, their blobs, and their associated Files for user's prints
            if (printIds.Count > 0)
            {
                var attachmentData = await _context.PrintAttachments
                    .Where(pa => printIds.Contains(pa.PrintId))
                    .Select(pa => new { pa.FileId, pa.File.Path })
                    .AsNoTracking()
                    .ToListAsync();

                foreach (var f in attachmentData)
                {
                    if (!string.IsNullOrEmpty(f.Path))
                    {
                        var parts = f.Path.Split('/', 2);
                        if (parts.Length == 2)
                        {
                            try
                            {
                                await _blobStorageService.DeleteBlobAsync(parts[0], parts[1]);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to delete blob {BlobPath} during user deletion; continuing", f.Path);
                            }
                        }
                    }
                }

                await _context.PrintAttachments
                    .Where(pa => printIds.Contains(pa.PrintId))
                    .ExecuteDeleteAsync();

                var attachmentFileIds = attachmentData.Select(f => f.FileId).ToList();
                if (attachmentFileIds.Count > 0)
                {
                    await _context.Files
                        .Where(f => attachmentFileIds.Contains(f.Id))
                        .ExecuteDeleteAsync();
                }
            }

            // Delete Prints
            await _context.Prints
                .Where(p => p.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete Projects and their images
            var projectIds = await _context.Projects
                .Where(p => p.CreatedById == userId)
                .Select(p => p.Id)
                .ToListAsync();

            if (projectIds.Count > 0)
            {
                // Nullify ProjectId on prints by other users that reference this user's projects
                await _context.Prints
                    .Where(p => p.ProjectId.HasValue && projectIds.Contains(p.ProjectId.Value))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ProjectId, (Guid?)null));

                // As for print images above. Older project rows store a bare blob name rather than
                // "container/name", which DeleteImageBlobsAsync handles.
                var projectImageData = await _context.ProjectImages
                    .Where(pi => projectIds.Contains(pi.ProjectId))
                    .Select(pi => new
                    {
                        pi.FileId,
                        pi.ThumbnailFileId,
                        pi.SocialFileId,
                        pi.File.Path,
                        ThumbnailPath = pi.ThumbnailFile != null ? pi.ThumbnailFile.Path : null,
                        SocialPath = pi.SocialFile != null ? pi.SocialFile.Path : null,
                    })
                    .AsNoTracking()
                    .ToListAsync();

                await DeleteImageBlobsAsync(
                    BlobContainers.ProjectImages,
                    projectImageData.SelectMany(f => new[] { f.Path, f.ThumbnailPath, f.SocialPath }));

                await _context.ProjectImages
                    .Where(pi => projectIds.Contains(pi.ProjectId))
                    .ExecuteDeleteAsync();

                var projectImageFileIds = projectImageData
                    .SelectMany(f => new[] { (Guid?)f.FileId, f.ThumbnailFileId, f.SocialFileId })
                    .OfType<Guid>()
                    .ToList();
                if (projectImageFileIds.Count > 0)
                {
                    await _context.Files
                        .Where(f => projectImageFileIds.Contains(f.Id))
                        .ExecuteDeleteAsync();
                }

                await _context.Projects
                    .Where(p => p.CreatedById == userId)
                    .ExecuteDeleteAsync();
            }

            // Delete PrinterFilament (loaded filaments) for user's printers
            await _context.PrinterFilament
                .Where(pf => pf.Printer.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete Printer Maintenance
            await _context.PrinterMaintenance
                .Where(pm => pm.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete PrinterImages, their blobs, and their associated Files for user's printers.
            // This MUST precede the Printers delete below: ExecuteDeleteAsync bypasses the change
            // tracker and hits the PrinterImage -> File Restrict FKs directly.
            var printerImageData = await _context.PrinterImages
                .Where(pi => pi.Printer.UserId == userId)
                .Select(pi => new
                {
                    pi.FileId,
                    pi.ThumbnailFileId,
                    Path = pi.File.Path,
                    ThumbnailPath = pi.ThumbnailFile != null ? pi.ThumbnailFile.Path : null
                })
                .AsNoTracking()
                .ToListAsync();

            if (printerImageData.Count > 0)
            {
                var printerBlobNames = printerImageData
                    .SelectMany(pi => new[] { pi.Path, pi.ThumbnailPath })
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Select(path => System.IO.Path.GetFileName(path)!);

                foreach (var name in printerBlobNames)
                {
                    try
                    {
                        await _blobStorageService.DeleteBlobAsync(BlobContainers.PrinterImages, name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete blob {BlobName} during user deletion; continuing", name);
                    }
                }

                await _context.PrinterImages
                    .Where(pi => pi.Printer.UserId == userId)
                    .ExecuteDeleteAsync();

                var printerImageFileIds = printerImageData
                    .SelectMany(pi => new[] { pi.FileId, pi.ThumbnailFileId })
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToList();

                if (printerImageFileIds.Count > 0)
                {
                    await _context.Files
                        .Where(f => printerImageFileIds.Contains(f.Id))
                        .ExecuteDeleteAsync();
                }
            }

            // Spoolman connections (#175) and their fair-use rows, before Printers for the same
            // reason as Connections below. The two fair-use tables would cascade with the user,
            // but are cleared here so the account's data is gone in one place.
            var spoolmanSecretHashes = await _context.SpoolmanConnections
                .Where(c => c.UserId == userId)
                .Select(c => c.SecretHash)
                .ToListAsync();
            await _context.SpoolmanConnections
                .Where(c => c.UserId == userId)
                .ExecuteDeleteAsync();

            // Evict the secrets (#194), or the credential cache keeps authenticating them for up
            // to its 1 h TTL. Each ExecuteDeleteAsync here commits on its own (there is no
            // enclosing transaction), so the rows are gone by now and a lookup that starts after
            // this sees no row. One already in flight (its SELECT ran before the delete, its cache
            // write lands after this) can still store "valid" until the TTL; every eviction site
            // has that same window. Evicting here rather than at the end means a later step
            // failing still leaves the deleted connections refused.
            await _spoolmanCredentials.InvalidateAsync(spoolmanSecretHashes);
            await _context.SpoolmanNetworkSightings
                .Where(s => s.UserId == userId)
                .ExecuteDeleteAsync();
            await _context.SpoolmanFairUseAlerts
                .Where(a => a.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete paired devices (#148) before Connections: their tokens stop working at once
            // (nothing caches them), so no heartbeat or check-in can link a connection to a bridge
            // after this, and their bridges (printlog-bridge#100) cascade with them, desired
            // printers included, before Printers.
            await DeleteDeviceGrantsAsync(userId);

            // Delete Connections. After Prints, whose FK to them does not cascade, and before
            // Printers, which their own FK would otherwise block.
            await _context.Connections
                .Where(c => c.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete Printers
            await _context.Printers
                .Where(p => p.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete FilamentAdjustments for user's filaments
            await _context.FilamentAdjustments
                .Where(fa => fa.Filament.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete FilamentImages, their blobs, and their associated Files for user's filaments.
            // This MUST precede the Filaments delete below: ExecuteDeleteAsync bypasses the change
            // tracker and hits the FilamentImage -> File Restrict FKs directly.
            var filamentImageData = await _context.FilamentImages
                .Where(fi => fi.Filament.CreatedById == userId)
                .Select(fi => new
                {
                    fi.FileId,
                    fi.ThumbnailFileId,
                    Path = fi.File.Path,
                    ThumbnailPath = fi.ThumbnailFile != null ? fi.ThumbnailFile.Path : null
                })
                .AsNoTracking()
                .ToListAsync();

            if (filamentImageData.Count > 0)
            {
                var blobNames = filamentImageData
                    .SelectMany(fi => new[] { fi.Path, fi.ThumbnailPath })
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Select(path => System.IO.Path.GetFileName(path)!);

                foreach (var name in blobNames)
                {
                    try
                    {
                        await _blobStorageService.DeleteBlobAsync(BlobContainers.FilamentImages, name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete blob {BlobName} during user deletion; continuing", name);
                    }
                }

                await _context.FilamentImages
                    .Where(fi => fi.Filament.CreatedById == userId)
                    .ExecuteDeleteAsync();

                var filamentImageFileIds = filamentImageData
                    .SelectMany(fi => new[] { fi.FileId, fi.ThumbnailFileId })
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToList();

                if (filamentImageFileIds.Count > 0)
                {
                    await _context.Files
                        .Where(f => filamentImageFileIds.Contains(f.Id))
                        .ExecuteDeleteAsync();
                }
            }

            // Delete FilamentTags. Their user FK is NoAction (a cascade would be a second path from
            // Users), so they must go before the filaments rather than relying on the filament cascade.
            await _context.FilamentTags
                .Where(t => t.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete this user's catalog SKU votes. The FK cascades too; the explicit delete keeps
            // this method the one list of what a user owns. The catalog entries stay.
            await _context.FilamentCatalogSkus
                .Where(s => s.UserId == userId)
                .ExecuteDeleteAsync();

            // Detach other users' usage rows from this user's spools, as deleting a spool does
            // (FilamentService.DeleteFilament): the row keeps its amount, with no spool. Its FK is
            // Restrict, so a legacy row like this (from before spool ownership was checked) would
            // otherwise fail this user's deletion on every run. This user's own prints and their
            // rows are already gone. The rows feed their owners' cached summaries and feed order.
            var foreignUsage = _context.PrintFilament
                .Where(pf => pf.Filament!.CreatedById == userId && pf.Print.CreatedById != userId);
            var foreignUsageOwners = await foreignUsage
                .Select(pf => pf.Print.CreatedById)
                .Distinct()
                .ToListAsync();
            if (foreignUsageOwners.Count > 0)
            {
                await foreignUsage.ExecuteUpdateAsync(setters => setters.SetProperty(pf => pf.FilamentId, (Guid?)null));
                foreach (var ownerId in foreignUsageOwners)
                {
                    _cacheVersionService.InvalidateUserCache(ownerId);
                }
            }

            // Delete Filaments
            await _context.Filaments
                .Where(f => f.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete API Keys
            await _context.UserApiKeys
                .Where(key => key.UserId == userId)
                .ExecuteDeleteAsync();

            // Profile and cover pictures, current and replaced: the user's own Files rows name their
            // blobs, and nothing else does. The picture columns are never used, as they may hold an
            // external URL (the Auth0 picture).
            var profilePicturePaths = await _context.Files
                .Where(f => f.CreatedById == userId && f.Path != null && f.Path.StartsWith(BlobContainers.UserProfile + "/"))
                .Select(f => f.Path)
                .ToListAsync();
            await DeleteImageBlobsAsync(BlobContainers.UserProfile, profilePicturePaths);

            // Delete remaining Files
            await _context.Files
                .Where(f => f.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete Feedback
            await _context.Feedback
                .Where(f => f.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete achievements. The FK cascades too; deleting explicitly keeps this method the
            // one place that lists everything a user owns.
            await _context.UserAchievements
                .Where(a => a.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete queued and sent email. The FK cascades too. Suppressions are keyed by address
            // hash, not user, and stay: a complaint must keep blocking the address after deletion.
            await _context.EmailOutbox
                .Where(o => o.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete webhook subscriptions (#235). The FK cascades too. An event not yet fanned out
            // finds no subscription, and a delivery already queued re-reads its row before every
            // attempt, the first included, and stops. An attempt already on the wire can still complete; nothing starts
            // after it.
            await _context.WebhookSubscriptions
                .Where(w => w.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete User Settings
            await _context.UserSettings
                .Where(us => us.UserId == userId)
                .ExecuteDeleteAsync();

            // Set TriggeredByUserId to null for notifications this user triggered
            await _context.Notifications
                .Where(n => n.TriggeredByUserId == userId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(n => n.TriggeredByUserId, (long?)null));

            // Stripe was canceled first (CancelStripeSubscriptionsAsync), so the record can go.
            await _context.Subscriptions
                .Where(s => s.UserId == userId)
                .ExecuteDeleteAsync();

            // Finally, delete the user
            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            // After the last write, so nothing cached on the old version GUID (summaries, the
            // Spoolman inventory) is served for the deleted account.
            _cacheVersionService.InvalidateUserCache(userId);

            _logger.LogInformation("Successfully deleted all data for user {userId}", userId);
        }
        finally
        {
            // Restore previous timeout
            _context.Database.SetCommandTimeout(previousTimeout);
        }
    }

    /// <summary>
    /// Cancels, immediately, every subscription of the user that Stripe may still bill: any with a
    /// Stripe id that is not canceled here, whatever its local status. A <c>past_due</c> one in
    /// particular keeps being retried by Stripe, and a retry that succeeds makes it active again,
    /// billing a customer with no account and no local row to reconcile it against.
    /// <para>
    /// A subscription Stripe does not know (404), or that it reports canceled after refusing the
    /// cancel, is done. Any other failure throws, so the user is not deleted: the subscription
    /// row is the only local record of what Stripe may still charge, and the next run retries.
    /// </para>
    /// </summary>
    private async Task CancelStripeSubscriptionsAsync(long userId)
    {
        var stripeSubscriptionIds = await _context.Subscriptions
            .Where(s => s.UserId == userId
                && s.StripeSubscriptionId != null
                && s.StripeSubscriptionId != ""
                && s.Status != SubscriptionStatus.Canceled)
            .Select(s => s.StripeSubscriptionId!)
            .AsNoTracking()
            .ToListAsync();

        if (stripeSubscriptionIds.Count == 0)
        {
            return;
        }

        var stripeService = new Stripe.SubscriptionService();
        foreach (var stripeSubscriptionId in stripeSubscriptionIds)
        {
            try
            {
                await stripeService.CancelAsync(stripeSubscriptionId);
            }
            catch (Stripe.StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning(ex, "Stripe has no subscription {StripeSubscriptionId} for user {UserId}; nothing to cancel", stripeSubscriptionId, userId);
            }
            catch (Stripe.StripeException ex)
            {
                if (!await IsCanceledInStripeAsync(stripeService, stripeSubscriptionId))
                {
                    throw;
                }

                _logger.LogInformation(ex, "Stripe subscription {StripeSubscriptionId} of user {UserId} was already canceled", stripeSubscriptionId, userId);
            }
        }
    }

    /// <summary>Whether Stripe reports the subscription as ended; false when it cannot be read.</summary>
    private static async Task<bool> IsCanceledInStripeAsync(Stripe.SubscriptionService stripeService, string stripeSubscriptionId)
    {
        try
        {
            var subscription = await stripeService.GetAsync(stripeSubscriptionId);
            return subscription.Status is "canceled" or "incomplete_expired";
        }
        catch (Stripe.StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return true;
        }
        catch (Stripe.StripeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes print, project or profile image blobs from their fixed container. Paths are either
    /// "container/name" or a bare name; the file name is the blob name either way. Failures are
    /// logged and skipped, as everywhere else in account deletion.
    /// </summary>
    private async Task DeleteImageBlobsAsync(string containerName, IEnumerable<string?> paths)
    {
        var names = paths
            .Select(path => System.IO.Path.GetFileName(path))
            .OfType<string>()
            .Where(name => name.Length > 0);

        foreach (var name in names)
        {
            try
            {
                await _blobStorageService.DeleteBlobAsync(containerName, name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete blob {BlobName} during user deletion; continuing", name);
            }
        }
    }
}
