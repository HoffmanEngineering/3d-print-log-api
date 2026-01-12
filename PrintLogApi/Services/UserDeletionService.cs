using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PrintLogApi.Models;

namespace PrintLogApi.Services
{
    public class UserDeletionService : IUserDeletionService
    {
        private readonly PrintLogContext _context;
        private readonly ILogger<UserDeletionService> _logger;
        private readonly TelemetryClient _telemetry;
        private readonly IAuth0Service _auth0Service;
        private readonly int _deactivationTimeInMinutes;

        public UserDeletionService(PrintLogContext context,
                                   ILogger<UserDeletionService> logger,
                                   TelemetryClient telemetry,
                                   IConfiguration config,
                                   IAuth0Service auth0Service)
        {
            _context = context;
            _logger = logger;
            _telemetry = telemetry;
            _auth0Service = auth0Service;

            _deactivationTimeInMinutes = int.Parse(config["PendingUserDeactivationTimeInMinutes"], CultureInfo.InvariantCulture);
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

                // Delete PrintImages and their associated Files for user's prints
                if (printIds.Count > 0)
                {
                    var printImageFileIds = await _context.PrintImages
                        .Where(pi => printIds.Contains(pi.PrintId))
                        .Select(pi => pi.FileId)
                        .ToListAsync();

                    await _context.PrintImages
                        .Where(pi => printIds.Contains(pi.PrintId))
                        .ExecuteDeleteAsync();

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

                // Delete Prints
                await _context.Prints
                    .Where(p => p.CreatedById == userId)
                    .ExecuteDeleteAsync();

                // Delete PrinterFilament (loaded filaments) for user's printers
                await _context.PrinterFilament
                    .Where(pf => pf.Printer.UserId == userId)
                    .ExecuteDeleteAsync();

                // Delete Printer Maintenance
                await _context.PrinterMaintenance
                    .Where(pm => pm.CreatedById == userId)
                    .ExecuteDeleteAsync();

                // Delete Printers
                await _context.Printers
                    .Where(p => p.UserId == userId)
                    .ExecuteDeleteAsync();

                // Delete FilamentAdjustments for user's filaments
                await _context.FilamentAdjustments
                    .Where(fa => fa.Filament.CreatedById == userId)
                    .ExecuteDeleteAsync();

                // Delete Filaments
                await _context.Filaments
                    .Where(f => f.CreatedById == userId)
                    .ExecuteDeleteAsync();

                // Delete API Keys
                await _context.UserApiKeys
                    .Where(key => key.UserId == userId)
                    .ExecuteDeleteAsync();

                // Delete remaining Files
                await _context.Files
                    .Where(f => f.CreatedById == userId)
                    .ExecuteDeleteAsync();

                // Delete Feedback
                await _context.Feedback
                    .Where(f => f.CreatedById == userId)
                    .ExecuteDeleteAsync();

                // Delete User Settings
                await _context.UserSettings
                    .Where(us => us.UserId == userId)
                    .ExecuteDeleteAsync();

                // Finally, delete the user
                _context.Users.Remove(user);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Successfully deleted all data for user {userId}", userId);
            }
            finally
            {
                // Restore previous timeout
                _context.Database.SetCommandTimeout(previousTimeout);
            }
        }
    }
}
