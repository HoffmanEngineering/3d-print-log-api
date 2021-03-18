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
        private readonly int _deactivationTimeInMinutes;

        public UserDeletionService(PrintLogContext context, ILogger<UserDeletionService> logger, TelemetryClient telemetry, IConfiguration config )
        {
            _context = context;
            _logger = logger;
            _telemetry = telemetry;

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

            foreach (var user in usersToDelete)
            {
                _logger.LogInformation("Deleting User {id} with oauth id {oauthId}", user.Id, user.OAuthUserId);

                await DeleteAllDataForUser(user);

                // TODO: Send AUTH0 API request to delete that user.
            }

        }

        public async Task DeleteAllDataForUser(User user)
        {
            if (user is null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            long userId = user.Id;
            // Prints
            var prints = await _context.Prints
                .Include(p => p.Printer)
                .Include(p => p.Images)
                    .ThenInclude(p => p.File)
                .Include(p => p.Comments)
                    .ThenInclude(p => p.Comment)
                .Include(p => p.FilamentUsage)
                    .ThenInclude(pf => pf.Filament)
                .Where(p => p.CreatedById == userId)
                .ToListAsync();

            foreach (var print in prints)
            {
                // Remove Print Comments.
                foreach (var comment in print.Comments.ToArray())
                {
                    _context.Comments.Remove(comment.Comment);
                }
                _context.PrintComments.RemoveRange(print.Comments.ToArray());

                // Remove Print Images.
                foreach (var image in print.Images.ToArray())
                {
                    _context.Files.Remove(image.File);
                }
                _context.PrintImages.RemoveRange(print.Images.ToArray());

                // Remove PrintFilament for this print.
                _context.PrintFilament.RemoveRange(print.FilamentUsage.ToArray());

                _context.Prints.Remove(print);
            }

            // Other Print Comments.
            var comments = await _context.Comments
                .Where(c => c.CreatedById == userId)
                .ToListAsync();
            var commentIds = comments.Select(c => c.Id).ToList();
            var associatedPrintComments = await _context.PrintComments.Where(pc => commentIds.Contains(pc.CommentId)).ToListAsync();
            _context.Comments.RemoveRange(comments);
            _context.PrintComments.RemoveRange(associatedPrintComments);

            // Printers
            var printers = await _context.Printers.Where(p => p.UserId == userId).ToListAsync();
            _context.Printers.RemoveRange(printers);


            // Filament
            var filaments = await _context.Filaments
                .Include(f => f.FilamentAdjustments)
                .Where(f => f.CreatedById == userId)
                .ToListAsync();
            _context.Filaments.RemoveRange(filaments);

            // API Keys
            var keys = await _context.UserApiKeys.Where(key => key.UserId == userId).ToListAsync();
            _context.UserApiKeys.RemoveRange(keys);

            // Files
            var files = await _context.Files.Where(f => f.CreatedById == userId).ToListAsync();
            _context.Files.RemoveRange(files);

            // Feedbacks
            var feedbacks = await _context.Feedback.Where(f => f.CreatedById == userId).ToListAsync();
            _context.Feedback.RemoveRange(feedbacks);

            // User Settings.
            var userSettings = await _context.UserSettings.Where(u => u.UserId == userId).ToListAsync();
            _context.UserSettings.RemoveRange(userSettings);

            // Finally, remove the user.
            _context.Users.Remove(user);

            await _context.SaveChangesAsync();

        }
    }
}
