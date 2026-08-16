using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// Pins the SEP-2549 caching hints the SDK stamps on tools/list.
///
/// Our tool list is identity-dependent: AddAuthorizationFilters() hides the write tools from a
/// read-only token. A shared cache holding one response could therefore serve a read-only caller
/// the write-tool surface — disclosure of tool names, descriptions, and schemas it cannot invoke.
/// The call itself would still be refused, so this is a confidentiality concern rather than
/// privilege escalation, but private is the only correct scope for us.
///
/// These are SDK defaults rather than values we set, which normally would not warrant a test.
/// The exception is justified because a silent flip to public is a security regression, and
/// unlike a content-type assertion this cannot break on unrelated conformance updates.
/// </summary>
public class McpCachingHintsTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;

    public McpCachingHintsTests(McpDataWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ListTools_HintsAreZeroTtlAndPrivate()
    {
        await using var client = await _factory.ConnectAsync(
            IntegrationTestSeeder.TestUserOAuthId, new[] { "read:printdata" });

        // The lower-level overload returns the raw per-page result. The flattening overload
        // deliberately drops these fields.
        var result = await client.ListToolsAsync(new ListToolsRequestParams(), TestContext.Current.CancellationToken);

        Assert.Equal(CacheScope.Private, result.CacheScope);
        Assert.Equal(System.TimeSpan.Zero, result.TimeToLive);
    }
}
