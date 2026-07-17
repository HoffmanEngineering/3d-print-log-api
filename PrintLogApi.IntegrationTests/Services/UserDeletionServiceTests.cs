using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.User;

namespace PrintLogApi.IntegrationTests.Services
{
    public class UserDeletionServiceTests
    {
        [Fact]
        public async Task DeleteAllDataForUser_DeletesNotificationsReferencingUserPrintsBeforePrints()
        {
            var commandInterceptor = new CommandRecordingInterceptor();
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<PrintLogContext>()
                .UseSqlite(connection)
                .AddInterceptors(commandInterceptor)
                .Options;

            await using var context = new PrintLogContext(options);
            await context.Database.EnsureCreatedAsync();

            var user = new User
            {
                OAuthUserId = $"auth0|pending-delete-{Guid.NewGuid()}",
                ViewStatus = ProfileViewStatus.Public
            };
            var recipientUser = new User
            {
                OAuthUserId = $"auth0|notification-recipient-{Guid.NewGuid()}",
                ViewStatus = ProfileViewStatus.Public
            };
            context.Users.AddRange(user, recipientUser);
            await context.SaveChangesAsync();

            var printer = new Printer
            {
                Name = "Pending Delete Printer",
                UserId = user.Id,
                IsActive = true
            };
            context.Printers.Add(printer);
            await context.SaveChangesAsync();

            var now = DateTime.UtcNow;
            var print = new Print
            {
                Title = "Pending Delete Print",
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Public,
                PrinterId = printer.Id,
                CreatedById = user.Id,
                UpdatedById = user.Id,
                CreatedDate = now,
                UpdatedDate = now
            };
            context.Prints.Add(print);
            await context.SaveChangesAsync();

            context.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = recipientUser.Id,
                Type = NotificationType.PrintCompleted,
                Title = "Print Completed",
                Message = "A followed print completed.",
                IsRead = false,
                CreatedDate = now,
                PrintId = print.Id
            });
            await context.SaveChangesAsync();

            commandInterceptor.Clear();
            context.ChangeTracker.Clear();
            var userToDelete = await context.Users.SingleAsync(u => u.Id == user.Id);

            var service = new UserDeletionService(
                context,
                NullLogger<UserDeletionService>.Instance,
                new TelemetryClient(TelemetryConfiguration.CreateDefault()),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string>
                    {
                        ["PendingUserDeactivationTimeInMinutes"] = "1440"
                    })
                    .Build(),
                new TestAuth0Service(),
                new InMemoryBlobStorageService());

            await service.DeleteAllDataForUser(userToDelete);

            var notificationDeleteIndex = commandInterceptor.Commands.FindIndex(command => IsDeleteFrom(command, "Notifications"));
            var printDeleteIndex = commandInterceptor.Commands.FindIndex(command => IsDeleteFrom(command, "Prints"));

            Assert.True(notificationDeleteIndex >= 0, "Expected notifications to be deleted during user deletion.");
            Assert.True(printDeleteIndex >= 0, "Expected prints to be deleted during user deletion.");
            Assert.True(notificationDeleteIndex < printDeleteIndex, "Notifications referencing prints must be deleted before prints.");
        }

        private static bool IsDeleteFrom(string command, string tableName)
        {
            return command.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase)
                && command.Contains(tableName, StringComparison.OrdinalIgnoreCase);
        }

        private class CommandRecordingInterceptor : DbCommandInterceptor
        {
            public List<string> Commands { get; } = new List<string>();

            public void Clear()
            {
                Commands.Clear();
            }

            public override InterceptionResult<int> NonQueryExecuting(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<int> result)
            {
                Commands.Add(command.CommandText);
                return base.NonQueryExecuting(command, eventData, result);
            }

            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                Commands.Add(command.CommandText);
                return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            }
        }

        private class TestAuth0Service : IAuth0Service
        {
            public Task DeleteUser(string oauthUserId)
            {
                return Task.CompletedTask;
            }

            public Task<string> GetManagementApiBearerToken()
            {
                return Task.FromResult("test-token");
            }

            public Task<string> GetUserEmail(string oauthUserId, System.Threading.CancellationToken ct)
            {
                return Task.FromResult<string>(null);
            }

            public Task<System.Collections.Generic.IReadOnlyList<PrintLogApi.Models.DTOs.ConnectedAgentDto>> ListMcpGrants(
                string authUserId, System.Threading.CancellationToken ct)
            {
                return Task.FromResult<System.Collections.Generic.IReadOnlyList<PrintLogApi.Models.DTOs.ConnectedAgentDto>>(
                    new System.Collections.Generic.List<PrintLogApi.Models.DTOs.ConnectedAgentDto>());
            }

            public Task RevokeMcpGrant(string authUserId, string grantId, System.Threading.CancellationToken ct)
            {
                return Task.CompletedTask;
            }
        }
    }
}
