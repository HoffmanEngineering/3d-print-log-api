using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    public class McpLoggingTests : IClassFixture<McpLoggingTests.LoggingFactory>
    {
        private readonly LoggingFactory _factory;

        public McpLoggingTests(LoggingFactory factory) => _factory = factory;

        public sealed record Entry(string Tool, string Outcome, long DurationMs, string SubjectHash);

        public sealed class RecordingTelemetry : IMcpToolTelemetry
        {
            public ConcurrentBag<Entry> Entries { get; } = new();

            public void ToolCalled(string toolName, string outcome, long durationMs, string subjectHash) =>
                Entries.Add(new Entry(toolName, outcome, durationMs, subjectHash));
        }

        public sealed class LoggingFactory : McpDataWebApplicationFactory
        {
            public RecordingTelemetry Telemetry { get; } = new();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IMcpToolTelemetry>();
                    services.AddSingleton<IMcpToolTelemetry>(Telemetry);
                });
            }
        }

        [Fact]
        public async Task CallingTool_RecordsMcpToolCalled_WithSafeFields()
        {
            await using var client = await _factory.ConnectAsync();
            await client.CallToolAsync("ping", new Dictionary<string, object?> { ["message"] = "hi" });

            var entry = _factory.Telemetry.Entries.Single(e => e.Tool == "ping");
            Assert.Equal("success", entry.Outcome);
            Assert.True(entry.DurationMs >= 0);

            // Subject hash must be present but reveal neither the raw Auth0 subject nor the internal id.
            Assert.False(string.IsNullOrEmpty(entry.SubjectHash));
            Assert.NotEqual(IntegrationTestSeeder.TestUserOAuthId, entry.SubjectHash);
            Assert.DoesNotContain("auth0", entry.SubjectHash);
            // A hash, not the raw subject: fixed 16 hex chars.
            Assert.Equal(16, entry.SubjectHash.Length);
            Assert.Matches("^[0-9a-f]{16}$", entry.SubjectHash);
        }

        [Fact]
        public async Task ErroringTool_RecordsErrorOutcome()
        {
            await using var client = await _factory.ConnectAsync();
            await McpDataWebApplicationFactory.IsToolError(
                client, "get_print", new() { ["id"] = McpTestData.ForeignPrintId });

            var entry = _factory.Telemetry.Entries.First(e => e.Tool == "get_print");
            Assert.Equal("error", entry.Outcome);
        }
    }
}
