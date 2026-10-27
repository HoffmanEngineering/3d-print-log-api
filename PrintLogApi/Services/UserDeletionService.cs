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
    private readonly int _deactivationTimeInMinutes;

    public UserDeletionService(PrintLogContext context,
                               ILogger<UserDeletionService> logger,
                               TelemetryClient telemetry,
                               IConfiguration config,
                               IAuth0Service auth0Service,
                               IBlobStorageService blobStorageService,
                               ICacheVersionService cacheVersionService,
                               ISpoolmanCredentialCache spoolmanCredentials)
    {
        _context = context;
        _logger = logger;
        _telemetry = telemetry;
        _auth0Service = auth0Service;
        _blobStorageService = blobStorageService;
        _cacheVersionService = cacheVersionService;
        _spoolmanCredentials = spoolmanCredentials;

        // Null-forgiven: an absent PendingUserDeactivationTimeInMinutes is a deployment
        // misconfiguration that already threw here, at service construction.
        _deactivationTimeInMinutes = int.Parse(config["PendingUserDeactivationTimeInMinutes"]!, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task DeletePendingDeactivatedUsers()
    {
        var pendingDeactivationTime = DateTimeOffset.Now.AddMinutes(-_deactivationTimeInMinutes);

        // Find user's who's deactivation date is before the pending deactivation time.
        var usersToDelete = await _context.Users
            .Where(u => u.DeactivationDateTime != null && u.DeactivationDateTime <= pendingDeactivationTime)
            .ToListAsync();

        _logger.LogInformation("Deleting {count} deactivated users before {deactivationDate}", usersToDelete.Count, pendingDeactivationTime.ToString(CultureInfo.InvariantCulture));

        if (usersToDelete.Count > 0)
        {
            _telemetry.TrackEvent("DeletePendingDeactivatedUsers", new Dictionary<string, string>() { { "Count", usersToDelete.Count.ToString(CultureInfo.InvariantCulture) } });
        }


        foreach (var user in usersToDelete)
        {
            var internalId = user.Id;
            var oauthUserId = user.OAuthUserId;
            _logger.LogInformation("Deleting User {id} with oauth id {oauthId}", internalId, oauthUserId);

            // Delete all data from the database.
            await DeleteAllDataForUser(user);

            // Delete the user from the Auth Server.
            await _auth0Service.DeleteUser(oauthUserId);
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

            // Delete Connections. After Prints, whose FK to them does not cascade, and before
            // Printers, which their own FK would otherwise block.
            await _context.Connections
                .Where(c => c.UserId == userId)
                .ExecuteDeleteAsync();

            // Bridges (printlog-bridge#100) cascade from their device grants, but their desired
            // printers point at Printers with no cascade, so they go first: after the connections
            // that point at them, before Printers. Their child rows cascade from them.
            var grantIds = _context.DeviceGrants.Where(g => g.UserId == userId).Select(g => g.Id);
            await _context.Bridges
                .Where(b => grantIds.Contains(b.Id))
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

            // Delete Filaments
            await _context.Filaments
                .Where(f => f.CreatedById == userId)
                .ExecuteDeleteAsync();

            // Delete API Keys
            await _context.UserApiKeys
                .Where(key => key.UserId == userId)
                .ExecuteDeleteAsync();

            // Delete paired devices (#148). The FK cascades too; listed here so this method stays
            // the one list of what a user owns. Their tokens stop working at once: nothing caches them.
            await _context.DeviceGrants
                .Where(g => g.UserId == userId)
                .ExecuteDeleteAsync();

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

            // Cancel the active Stripe subscription, then delete the Subscription record
            var stripeSubscriptionId = await _context.Subscriptions
                .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
                .Select(s => s.StripeSubscriptionId!)
                .AsNoTracking()
                .SingleOrDefaultAsync();

            if (!string.IsNullOrEmpty(stripeSubscriptionId))
            {
                try
                {
                    var stripeService = new Stripe.SubscriptionService();
                    await stripeService.CancelAsync(stripeSubscriptionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cancel Stripe subscription {StripeSubscriptionId} during user deletion; continuing", stripeSubscriptionId);
                }
            }

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
    /// Deletes print or project image blobs from their fixed container. Paths are either
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
