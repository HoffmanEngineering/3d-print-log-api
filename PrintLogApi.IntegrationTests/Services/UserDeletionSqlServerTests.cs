using Microsoft.EntityFrameworkCore;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// Account deletion's cross-user cleanups, executed against real SQL Server with the migrated
/// schema's foreign keys, which SQLite cannot vouch for: the Stripe subscription read, another
/// user's usage row detached from the user's spool (an ExecuteUpdate over joins, then the
/// Restrict FK on Filaments), another user's comment on the user's print with a notification
/// (whose FK to the comment does not cascade), MCP idempotency records, and a profile picture.
/// Skipped unless PRINTLOG_SQLSERVER_TEST_CONNECTION is set; reuses the migrated scratch host.
/// </summary>
public class UserDeletionSqlServerTests(EmailPipelineSqlServerTests.SqlServerFactory factory)
    : IClassFixture<EmailPipelineSqlServerTests.SqlServerFactory>
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("PRINTLOG_SQLSERVER_TEST_CONNECTION");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeleteAllDataForUser_CleansUpOtherUsersRowsAndPictures_OnSqlServer()
    {
        Assert.SkipUnless(Connection is not null, "Set PRINTLOG_SQLSERVER_TEST_CONNECTION to run against SQL Server.");

        var blobs = (InMemoryBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();
        var now = DateTime.UtcNow;
        long deletedId, otherId, otherPrintId, ownCommentOnOtherPrintId, otherCommentOnOwnPrintId, otherCommentElsewhereId;
        Guid usageId;
        string picturePath;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var deleted = new User { OAuthUserId = $"auth0|delete-sql-{Guid.NewGuid():N}", ViewStatus = User.ProfileViewStatus.Public };
            var other = new User { OAuthUserId = $"auth0|keep-sql-{Guid.NewGuid():N}", ViewStatus = User.ProfileViewStatus.Public };
            db.Users.AddRange(deleted, other);
            await db.SaveChangesAsync(Ct);
            deletedId = deleted.Id;
            otherId = other.Id;

            var deletedPrinter = new Printer { Name = "Deleted user's printer", UserId = deletedId, IsActive = true };
            var otherPrinter = new Printer { Name = "Other user's printer", UserId = otherId, IsActive = true };
            var spool = new Filament
            {
                DisplayName = "Deleted user's spool",
                MaterialType = "PLA",
                MaterialCategoryNickname = "filament",
                CreatedById = deletedId,
                UpdatedById = deletedId,
                CreatedDate = now,
                UpdatedDate = now,
            };
            db.AddRange(deletedPrinter, otherPrinter, spool);
            await db.SaveChangesAsync(Ct);

            var ownPrint = NewPrint(deletedId, deletedPrinter.Id, now);
            var otherPrint = NewPrint(otherId, otherPrinter.Id, now);
            db.Prints.AddRange(ownPrint, otherPrint);
            await db.SaveChangesAsync(Ct);
            otherPrintId = otherPrint.Id;

            // B3: the other user's print uses the deleted user's spool (a legacy cross-user row).
            var usage = new PrintFilament { Id = Guid.NewGuid(), PrintId = otherPrint.Id, FilamentId = spool.Id, AmountMg = 4_321 };
            db.PrintFilament.Add(usage);
            usageId = usage.Id;

            // B8: the other user's comment on the deleted user's print, with a notification that
            // names only the comment; the deleted user's comment on the other print; and the other
            // user's comment on their own print, which stays.
            var otherOnOwn = NewComment(otherId, "on the deleted user's print", now);
            var ownOnOther = NewComment(deletedId, "on the other user's print", now);
            var otherOnOther = NewComment(otherId, "on their own print", now);
            db.Comments.AddRange(otherOnOwn, ownOnOther, otherOnOther);
            await db.SaveChangesAsync(Ct);
            otherCommentOnOwnPrintId = otherOnOwn.Id;
            ownCommentOnOtherPrintId = ownOnOther.Id;
            otherCommentElsewhereId = otherOnOther.Id;
            db.PrintComments.AddRange(
                NewLink(ownPrint.Id, otherOnOwn.Id, otherId, now),
                NewLink(otherPrint.Id, ownOnOther.Id, deletedId, now),
                NewLink(otherPrint.Id, otherOnOther.Id, otherId, now));
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = otherId,
                Type = NotificationType.PrintCompleted,
                Title = "About a comment",
                Message = "m",
                CreatedDate = now,
                CommentId = otherOnOwn.Id,
            });

            // B8: MCP idempotency records, one each.
            db.McpIdempotencyRecords.AddRange(
                new McpIdempotencyRecord { UserId = deletedId, ToolName = "create_print", IdempotencyKey = $"del-{Guid.NewGuid():N}", CreatedPrintId = ownPrint.Id, CreatedAt = DateTimeOffset.UtcNow },
                new McpIdempotencyRecord { UserId = otherId, ToolName = "create_print", IdempotencyKey = $"keep-{Guid.NewGuid():N}", CreatedPrintId = otherPrint.Id, CreatedAt = DateTimeOffset.UtcNow });

            // B1: a canceled subscription, so the subscription read runs without a Stripe call.
            db.Subscriptions.Add(new Subscription
            {
                UserId = deletedId,
                Status = SubscriptionStatus.Canceled,
                Plan = SubscriptionPlan.ProMonthly,
                StripeCustomerId = $"cus_{deletedId}",
                StripeSubscriptionId = $"sub_{deletedId}",
                CreatedById = deletedId,
                UpdatedById = deletedId,
            });

            // B2: a profile picture, its blob and its Files row.
            var blobName = $"{Guid.NewGuid()}.jpg";
            using (var stream = new MemoryStream([1, 2, 3]))
            {
                picturePath = (await blobs.UploadAsync(BlobContainers.UserProfile, blobName, stream)).BlobPath;
            }

            db.Files.Add(new Models.File { Id = Guid.NewGuid(), Path = picturePath, Size = 3, CreatedById = deletedId, UpdatedById = deletedId });
            deleted.ProfilePicture = new Uri(blobs.BaseUri, picturePath).AbsoluteUri;
            await db.SaveChangesAsync(Ct);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await db.Users.SingleAsync(u => u.Id == deletedId, Ct);
            await scope.ServiceProvider.GetRequiredService<IUserDeletionService>().DeleteAllDataForUser(user);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Id == deletedId, Ct));
            Assert.True(await db.Users.AnyAsync(u => u.Id == otherId, Ct));

            var usage = await db.PrintFilament.AsNoTracking().SingleAsync(pf => pf.Id == usageId, Ct);
            Assert.Null(usage.FilamentId);
            Assert.Equal(4_321, usage.AmountMg);
            Assert.Equal(otherPrintId, usage.PrintId);
            Assert.False(await db.Filaments.IgnoreQueryFilters().AnyAsync(f => f.CreatedById == deletedId, Ct));

            var commentIds = new[] { otherCommentOnOwnPrintId, ownCommentOnOtherPrintId, otherCommentElsewhereId };
            Assert.Equal([otherCommentElsewhereId], await db.Comments.Where(c => commentIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(Ct));
            Assert.False(await db.Notifications.AnyAsync(n => n.CommentId == otherCommentOnOwnPrintId, Ct));

            Assert.False(await db.McpIdempotencyRecords.AnyAsync(r => r.UserId == deletedId, Ct));
            Assert.True(await db.McpIdempotencyRecords.AnyAsync(r => r.UserId == otherId, Ct));
            Assert.False(await db.Subscriptions.AnyAsync(s => s.UserId == deletedId, Ct));
            Assert.False(await db.Files.AnyAsync(f => f.CreatedById == deletedId, Ct));
        }

        Assert.False(blobs.BlobExists(picturePath), picturePath);
    }

    private static Print NewPrint(long ownerId, long printerId, DateTime now) => new()
    {
        Title = $"print of {ownerId}",
        Status = Print.PrintStatus.Success,
        ViewStatus = Print.PrintViewStatus.Public,
        PrinterId = printerId,
        CreatedById = ownerId,
        UpdatedById = ownerId,
        CreatedDate = now,
        UpdatedDate = now,
    };

    private static Comment NewComment(long authorId, string body, DateTime now) =>
        new() { Body = body, CreatedById = authorId, UpdatedById = authorId, CreatedDate = now, UpdatedDate = now };

    private static PrintComment NewLink(long printId, long commentId, long authorId, DateTime now) =>
        new() { PrintId = printId, CommentId = commentId, CreatedById = authorId, UpdatedById = authorId, CreatedDate = now, UpdatedDate = now };
}
