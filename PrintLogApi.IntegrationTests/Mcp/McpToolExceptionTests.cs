using PrintLogApi.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

public class McpToolExceptionTests
{
    [Fact]
    public void Conflict_HasStableCode()
    {
        var ex = McpToolException.Conflict("reused key with different arguments");
        Assert.Equal("conflict", ex.Code);
        Assert.Equal("reused key with different arguments", ex.Message);
    }
}
