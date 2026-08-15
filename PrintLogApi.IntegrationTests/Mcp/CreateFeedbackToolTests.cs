using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// End-to-end tests for create_feedback through /mcp.
    /// <para>
    /// The fixture's email sender and Auth0 stub are singletons shared by every test here, so each
    /// test tags its note with a unique marker and asserts only on the emails carrying it. Counting
    /// everything sent would couple these tests to each other.
    /// </para>
    /// </summary>
    public class CreateFeedbackToolTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;

        public CreateFeedbackToolTests(McpDataWebApplicationFactory factory) => _factory = factory;

        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };
        private static readonly string[] WriteOnly = { "write:printdata" };

        private static string Marker() => $"MARK-{Guid.NewGuid():N}";

        [Fact]
        public async Task CreateFeedback_RecordsFeedbackOwnedByTheTokenUser()
        {
            var marker = Marker();
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
            {
                ["type"] = "Suggestion",
                ["note"] = $"Please add a dark mode {marker}",
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
            });

            Assert.True(result.IsError != true);
            using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text);
            var feedback = doc.RootElement.GetProperty("feedback");
            Assert.Equal("Suggestion", feedback.GetProperty("type").GetString());
            Assert.Contains(marker, feedback.GetProperty("note").GetString());
            Assert.False(doc.RootElement.GetProperty("wasReplayed").GetBoolean());

            var id = Guid.Parse(feedback.GetProperty("feedbackId").GetString()!);
            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var row = await context.Feedback.AsNoTracking().FirstAsync(f => f.Id == id);
            Assert.Equal(IntegrationTestSeeder.TestUserId, row.CreatedById);
            // An agent submits no form, so the "what the user typed" column stays empty.
            Assert.Null(row.Email);
        }

        // The echo is the only way a write-only agent can confirm what it sent: there is no
        // get_feedback, and it cannot call the read tools at all.
        [Fact]
        public async Task CreateFeedback_WriteOnlyToken_CanReadBackWhatItSent()
        {
            var marker = Marker();
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, WriteOnly);

            var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
            {
                ["type"] = "Bug",
                ["note"] = $"The spool weight resets {marker}",
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
            });

            Assert.True(result.IsError != true);
            using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text);
            var feedback = doc.RootElement.GetProperty("feedback");
            Assert.Equal("Bug", feedback.GetProperty("type").GetString());
            Assert.Contains(marker, feedback.GetProperty("note").GetString());
            Assert.NotEqual(Guid.Empty, Guid.Parse(feedback.GetProperty("feedbackId").GetString()!));
        }

        // The whole reason the key is REQUIRED here: the email cannot be taken back, so a retry must
        // not send a second one.
        [Fact]
        public async Task CreateFeedback_SameKeyAndArgs_ReplaysAndNotifiesExactlyOnce()
        {
            var marker = Marker();
            var key = Guid.NewGuid().ToString();
            var args = new Dictionary<string, object?>
            {
                ["type"] = "Question",
                ["note"] = $"How do I export? {marker}",
                ["idempotencyKey"] = key,
            };
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var first = await client.CallToolAsync("create_feedback", args);
            var second = await client.CallToolAsync("create_feedback", args);

            Assert.True(first.IsError != true);
            Assert.True(second.IsError != true);

            using var firstDoc = JsonDocument.Parse(first.Content.OfType<TextContentBlock>().First().Text);
            using var secondDoc = JsonDocument.Parse(second.Content.OfType<TextContentBlock>().First().Text);
            Assert.False(firstDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
            Assert.True(secondDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
            Assert.Equal(
                firstDoc.RootElement.GetProperty("feedback").GetProperty("feedbackId").GetString(),
                secondDoc.RootElement.GetProperty("feedback").GetProperty("feedbackId").GetString());

            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Assert.Equal(1, await context.Feedback.CountAsync(f => f.Note!.Contains(marker)));
            Assert.Single(_factory.EmailSender.Matching(marker));
        }

        [Fact]
        public async Task CreateFeedback_SameKeyDifferentArgs_IsConflict()
        {
            var key = Guid.NewGuid().ToString();
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var first = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
            {
                ["type"] = "Question",
                ["note"] = $"Original {Marker()}",
                ["idempotencyKey"] = key,
            });
            Assert.True(first.IsError != true);

            var second = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
            {
                ["type"] = "Question",
                ["note"] = $"Completely different {Marker()}",
                ["idempotencyKey"] = key,
            });

            Assert.True(second.IsError == true);
            var text = second.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("different arguments", text, StringComparison.OrdinalIgnoreCase);
        }

        // A key is required, unlike every other create tool.
        [Fact]
        public async Task CreateFeedback_WithoutIdempotencyKey_IsInvalidArguments()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, "create_feedback",
                new Dictionary<string, object?>
                {
                    ["type"] = "Bug",
                    ["note"] = "no key supplied",
                }));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateFeedback_BlankNote_IsInvalidArguments(string note)
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, "create_feedback",
                new Dictionary<string, object?>
                {
                    ["type"] = "Bug",
                    ["note"] = note,
                    ["idempotencyKey"] = Guid.NewGuid().ToString(),
                }));
        }

        [Fact]
        public async Task CreateFeedback_NoteOverMaxLength_IsInvalidArguments()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            Assert.True(await McpDataWebApplicationFactory.IsToolError(client, "create_feedback",
                new Dictionary<string, object?>
                {
                    ["type"] = "Other",
                    ["note"] = new string('x', 5001),
                    ["idempotencyKey"] = Guid.NewGuid().ToString(),
                }));
        }

        // Nothing lists the feedback types, so the rejection has to name them.
        [Fact]
        public async Task CreateFeedback_UnknownType_NamesTheValidTypes()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
            {
                ["type"] = 99,
                ["note"] = "an undefined type",
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
            });

            Assert.True(result.IsError == true);
            var text = result.Content.OfType<TextContentBlock>().First().Text;
            Assert.Contains("Question", text);
            Assert.Contains("Bug", text);
            Assert.Contains("Suggestion", text);
            Assert.Contains("Other", text);
        }

        [Fact]
        public async Task CreateFeedback_NotifiesWithTheAuth0AddressAndAgentSource()
        {
            var marker = Marker();
            _factory.Auth0.ThrowOnGetUserEmail = false;
            _factory.Auth0.UserEmail = "account-holder@example.test";
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
            {
                ["type"] = "Suggestion",
                ["note"] = $"notify me {marker}",
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
            });
            Assert.True(result.IsError != true);

            var email = Assert.Single(_factory.EmailSender.Matching(marker));
            Assert.Equal(CustomWebApplicationFactory.TestFeedbackEmailAddress, email.To);
            Assert.Contains("account-holder@example.test", email.Body);
            Assert.Contains("Submitted via: MCP agent", email.Body);
        }

        // An Auth0 outage or a missing read:users grant must cost us the address, not the feedback.
        [Fact]
        public async Task CreateFeedback_Auth0LookupFails_StillRecordsAndNotifies()
        {
            var marker = Marker();
            _factory.Auth0.ThrowOnGetUserEmail = true;
            try
            {
                await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

                var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
                {
                    ["type"] = "Bug",
                    ["note"] = $"auth0 is down {marker}",
                    ["idempotencyKey"] = Guid.NewGuid().ToString(),
                });

                Assert.True(result.IsError != true);
                var email = Assert.Single(_factory.EmailSender.Matching(marker));
                Assert.Contains("(not available)", email.Body);

                using var scope = _factory.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                Assert.Equal(1, await context.Feedback.CountAsync(f => f.Note!.Contains(marker)));
            }
            finally
            {
                _factory.Auth0.ThrowOnGetUserEmail = false;
            }
        }

        // The row is the source of truth. Failing the call would not undo it — it would only lie to
        // the caller and burn the key, so the retry replays and never notifies either.
        [Fact]
        public async Task CreateFeedback_NotificationSendFails_StillRecordsAndReportsSuccess()
        {
            var marker = Marker();
            _factory.EmailSender.ThrowOnSend = true;
            try
            {
                await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

                var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
                {
                    ["type"] = "Other",
                    ["note"] = $"smtp is down {marker}",
                    ["idempotencyKey"] = Guid.NewGuid().ToString(),
                });

                Assert.True(result.IsError != true);

                using var scope = _factory.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                Assert.Equal(1, await context.Feedback.CountAsync(f => f.Note!.Contains(marker)));
            }
            finally
            {
                _factory.EmailSender.ThrowOnSend = false;
            }
        }

        // The notification runs AFTER the commit, so a cancellation-shaped failure there must be
        // treated like any other: the row exists either way. Letting it escape would report failure
        // for feedback that was saved AND burn the key, so the retry replays and never notifies —
        // the notification would be unrecoverable. A generic-exception test does not cover this:
        // OperationCanceledException is exactly what a client disconnect and an HttpClient timeout
        // both arrive as.
        [Fact]
        public async Task CreateFeedback_Auth0LookupCancelled_StillRecordsAndNotifies()
        {
            var marker = Marker();
            _factory.Auth0.ThrowCancelledOnGetUserEmail = true;
            try
            {
                await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

                var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
                {
                    ["type"] = "Bug",
                    ["note"] = $"auth0 cancelled {marker}",
                    ["idempotencyKey"] = Guid.NewGuid().ToString(),
                });

                Assert.True(result.IsError != true);
                var email = Assert.Single(_factory.EmailSender.Matching(marker));
                Assert.Contains("(not available)", email.Body);

                using var scope = _factory.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                Assert.Equal(1, await context.Feedback.CountAsync(f => f.Note!.Contains(marker)));
            }
            finally
            {
                _factory.Auth0.ThrowCancelledOnGetUserEmail = false;
            }
        }

        [Fact]
        public async Task CreateFeedback_NotificationCancelled_StillRecordsAndReportsSuccess()
        {
            var marker = Marker();
            var key = Guid.NewGuid().ToString();
            _factory.EmailSender.ThrowCancelledOnSend = true;
            try
            {
                await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

                var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
                {
                    ["type"] = "Other",
                    ["note"] = $"send cancelled {marker}",
                    ["idempotencyKey"] = key,
                });

                Assert.True(result.IsError != true);

                using var scope = _factory.Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                Assert.Equal(1, await context.Feedback.CountAsync(f => f.Note!.Contains(marker)));

                // The key must still be usable as a key: a retry replays the committed row rather
                // than writing a second one.
                _factory.EmailSender.ThrowCancelledOnSend = false;
                var retry = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
                {
                    ["type"] = "Other",
                    ["note"] = $"send cancelled {marker}",
                    ["idempotencyKey"] = key,
                });
                Assert.True(retry.IsError != true);
                using var doc = JsonDocument.Parse(retry.Content.OfType<TextContentBlock>().First().Text);
                Assert.True(doc.RootElement.GetProperty("wasReplayed").GetBoolean());
                Assert.Equal(1, await context.Feedback.CountAsync(f => f.Note!.Contains(marker)));
            }
            finally
            {
                _factory.EmailSender.ThrowCancelledOnSend = false;
            }
        }

        [Fact]
        public async Task CreateFeedback_TrimsTheNoteItStores()
        {
            var marker = Marker();
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

            var result = await client.CallToolAsync("create_feedback", new Dictionary<string, object?>
            {
                ["type"] = "Other",
                ["note"] = $"   padded {marker}   ",
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
            });

            Assert.True(result.IsError != true);
            using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text);
            var note = doc.RootElement.GetProperty("feedback").GetProperty("note").GetString();
            Assert.Equal($"padded {marker}", note);
        }
    }
}
