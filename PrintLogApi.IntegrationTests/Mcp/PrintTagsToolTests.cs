using System.Text.Json;
using ModelContextProtocol.Protocol;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// Print tags on the read tools (UI #7): get_print names them, search_prints filters by one.
/// Tags are created straight in the database with unique names, since the host is shared.
/// </summary>
public class PrintTagsToolTests(McpDataWebApplicationFactory factory) : IClassFixture<McpDataWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Detail(long Id, List<string>? Tags);

    private sealed record Item(long Id);

    private sealed record PageResult(List<Item> Items, int TotalCount);

    private static T Parse<T>(CallToolResult result) =>
        JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, JsonOptions)!;

    private async Task<Tag> TagPrint(long userId, long printId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var tag = new Tag
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = Tag.Normalize(name),
            CreatedById = userId,
            UpdatedById = userId,
        };
        context.Tags.Add(tag);
        context.PrintTags.Add(new PrintTag { PrintId = printId, TagId = tag.Id });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return tag;
    }

    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..8]}";

    [Fact]
    public async Task GetPrint_ListsTheTagNames()
    {
        var name = Unique("Gifts");
        // Connect first: it starts the host, which is what seeds McpTestData's ids.
        await using var client = await factory.ConnectAsync();
        await TagPrint(IntegrationTestSeeder.TestUserId, McpTestData.SoapDishPrintId, name);
        var detail = Parse<Detail>(await client.CallToolAsync("get_print",
            new Dictionary<string, object?> { ["id"] = McpTestData.SoapDishPrintId },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(name, detail.Tags!);
    }

    [Fact]
    public async Task SearchPrints_FiltersByTagName_IgnoringCase()
    {
        var name = Unique("Cosplay");
        // Connect first: it starts the host, which is what seeds McpTestData's ids.
        await using var client = await factory.ConnectAsync();
        await TagPrint(IntegrationTestSeeder.TestUserId, McpTestData.ProjectPrintId, name);
        var page = Parse<PageResult>(await client.CallToolAsync("search_prints",
            new Dictionary<string, object?> { ["tag"] = $"  {name.ToUpperInvariant()} " },
            cancellationToken: TestContext.Current.CancellationToken));

        var item = Assert.Single(page.Items);
        Assert.Equal(McpTestData.ProjectPrintId, item.Id);
    }

    [Fact]
    public async Task SearchPrints_ByAnotherUsersTagName_FindsNothing()
    {
        // The caller has a print, another user has a tag by this name on one of theirs: the
        // filter is the caller's own tags only, so a name cannot probe someone else's labels.
        var name = Unique("Foreign");
        // Connect first: it starts the host, which is what seeds McpTestData's ids.
        await using var client = await factory.ConnectAsync();
        await TagPrint(McpTestData.OtherUserId, McpTestData.ForeignPrintId, name);
        var page = Parse<PageResult>(await client.CallToolAsync("search_prints",
            new Dictionary<string, object?> { ["tag"] = name },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task SearchPrints_RejectsABlankTag()
    {
        await using var client = await factory.ConnectAsync();

        Assert.True(await McpDataWebApplicationFactory.IsToolError(client, "search_prints",
            new Dictionary<string, object?> { ["tag"] = "   " }));
    }
}
