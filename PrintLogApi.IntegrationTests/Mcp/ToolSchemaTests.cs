using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp
{
    /// <summary>
    /// The advertised input schema is the only contract an agent can read before calling. These tests
    /// pin the places where a generated schema would otherwise overstate what a caller must send.
    /// </summary>
    public class ToolSchemaTests : IClassFixture<McpDataWebApplicationFactory>
    {
        private readonly McpDataWebApplicationFactory _factory;
        public ToolSchemaTests(McpDataWebApplicationFactory factory) => _factory = factory;
        private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

        /// <summary>
        /// A material usage row needs only the material id plus at least one complete amount pair,
        /// which the service enforces. The SDK derives 'required' from constructor parameters that
        /// have no default, so a positional record with none marked every field required — including
        /// the nullable ones. The server accepted an omitted 'notes' either way, so the schema was
        /// lying, and an agent reading it sends "notes": null on every row to satisfy a rule that
        /// does not exist.
        /// </summary>
        [Fact]
        public async Task CreatePrint_MaterialRow_RequiresOnlyTheMaterialId()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var tools = await client.ListToolsAsync();

            var createPrint = tools.Single(t => t.Name == "create_print");
            var required = createPrint.ProtocolTool.InputSchema
                .GetProperty("properties").GetProperty("materials")
                .GetProperty("items").GetProperty("required")
                .EnumerateArray().Select(e => e.GetString()).ToArray();

            Assert.Equal(new[] { "materialId" }, required);
        }

        [Fact]
        public async Task UpdatePrint_MaterialRow_RequiresOnlyTheMaterialId()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var tools = await client.ListToolsAsync();

            var updatePrint = tools.Single(t => t.Name == "update_print");
            var required = updatePrint.ProtocolTool.InputSchema
                .GetProperty("properties").GetProperty("materials")
                .GetProperty("items").GetProperty("required")
                .EnumerateArray().Select(e => e.GetString()).ToArray();

            Assert.Equal(new[] { "materialId" }, required);
        }

        /// <summary>
        /// create_feedback is the only create tool whose idempotencyKey is mandatory, and the schema
        /// is where an agent learns that. The same SDK rule applies in reverse here: a parameter is
        /// required precisely because it has no C# default, so giving idempotencyKey one would
        /// silently downgrade it to optional and reintroduce duplicate-notification retries.
        /// </summary>
        [Fact]
        public async Task CreateFeedback_RequiresTypeNoteAndIdempotencyKey()
        {
            await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
            var tools = await client.ListToolsAsync();

            var createFeedback = tools.Single(t => t.Name == "create_feedback");
            var required = createFeedback.ProtocolTool.InputSchema
                .GetProperty("required")
                .EnumerateArray().Select(e => e.GetString()).ToArray();

            Assert.Equal(new[] { "type", "note", "idempotencyKey" }.OrderBy(x => x), required.OrderBy(x => x));
        }
    }
}
