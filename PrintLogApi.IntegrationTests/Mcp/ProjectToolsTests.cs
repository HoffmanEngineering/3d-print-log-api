using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>End-to-end tests for list_projects, create_project, and update_project.</summary>
public class ProjectToolsTests : IClassFixture<McpDataWebApplicationFactory>
{
    private readonly McpDataWebApplicationFactory _factory;

    public ProjectToolsTests(McpDataWebApplicationFactory factory) => _factory = factory;

    private static readonly string[] ReadWrite = { "read:printdata", "write:printdata" };

    [Fact]
    public async Task ListProjects_ReturnsOwnBySearch_ExcludesForeign()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        var result = await client.CallToolAsync("list_projects", new Dictionary<string, object?>
        {
            ["search"] = "Rocket",
        }, cancellationToken: TestContext.Current.CancellationToken);

        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("Rocket Build", text);
        Assert.DoesNotContain("SECRET FOREIGN PROJECT", text);
    }

    [Fact]
    public async Task CreateProject_EchoesResultingVisibility()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        var result = await client.CallToolAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = "Agent Created Project",
            ["viewStatus"] = "Unlisted",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError != true);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.Contains("Unlisted", text);
    }

    // There is no get_project, so the create/update echo is the ONLY way a caller can confirm what
    // it wrote. Echoing name and status but not the three fields it just set made that impossible.
    [Fact]
    public async Task CreateProject_EchoesEverySettableField()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        var result = await client.CallToolAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = "Echo Everything",
            ["reference"] = "REF-42",
            ["description"] = "the full description",
            ["url"] = "https://example.com/thing",
            ["status"] = "Complete",
            ["viewStatus"] = "Public",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError != true);
        using var doc = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text);
        var project = doc.RootElement.GetProperty("project");
        Assert.Equal("Echo Everything", project.GetProperty("name").GetString());
        Assert.Equal("REF-42", project.GetProperty("reference").GetString());
        Assert.Equal("the full description", project.GetProperty("description").GetString());
        Assert.Equal("https://example.com/thing", project.GetProperty("url").GetString());
        Assert.Equal("Complete", project.GetProperty("status").GetString());
        Assert.Equal("Public", project.GetProperty("viewStatus").GetString());
        Assert.False(doc.RootElement.GetProperty("wasReplayed").GetBoolean());
    }

    [Fact]
    public async Task UpdateProject_EchoesEverySettableField()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        var created = await client.CallToolAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = "Update Echo Target",
        }, cancellationToken: TestContext.Current.CancellationToken);
        Guid id;
        using (var doc = JsonDocument.Parse(created.Content.OfType<TextContentBlock>().First().Text))
        {
            id = doc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid();
        }

        var updated = await client.CallToolAsync("update_project", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["reference"] = "REF-99",
            ["url"] = "https://example.com/updated",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(updated.IsError != true);
        using var updatedDoc = JsonDocument.Parse(updated.Content.OfType<TextContentBlock>().First().Text);
        // update_project returns the project unwrapped — it has nothing to replay.
        Assert.Equal("REF-99", updatedDoc.RootElement.GetProperty("reference").GetString());
        Assert.Equal("https://example.com/updated", updatedDoc.RootElement.GetProperty("url").GetString());
        Assert.Equal("Update Echo Target", updatedDoc.RootElement.GetProperty("name").GetString());
    }

    // Without a key a retried create silently duplicates — the other three create tools all take
    // one, and the dev database already collected two projects both named "Test Project 1".
    [Fact]
    public async Task CreateProject_SameKeyAndArguments_Replays()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        Dictionary<string, object?> Args() => new()
        {
            ["name"] = "Idempotent Project",
            ["idempotencyKey"] = "proj-key-1",
        };

        var first = await client.CallToolAsync("create_project", Args(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(first.IsError != true);
        var replay = await client.CallToolAsync("create_project", Args(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(replay.IsError != true);

        using var firstDoc = JsonDocument.Parse(first.Content.OfType<TextContentBlock>().First().Text);
        using var replayDoc = JsonDocument.Parse(replay.Content.OfType<TextContentBlock>().First().Text);

        Assert.False(firstDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
        Assert.True(replayDoc.RootElement.GetProperty("wasReplayed").GetBoolean());
        Assert.Equal(
            firstDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid(),
            replayDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid());
    }

    [Fact]
    public async Task CreateProject_SameKeyDifferentArguments_Conflicts()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        await client.CallToolAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = "Conflict Project",
            ["idempotencyKey"] = "proj-key-2",
        }, cancellationToken: TestContext.Current.CancellationToken);

        var conflict = await client.CallToolAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = "Conflict Project CHANGED",
            ["idempotencyKey"] = "proj-key-2",
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Asserted on the raw prefix, not via ToolErrorCode: that helper only recognizes
        // "not found" and "denied", and reports every other failure as a bare "error" — it can
        // never distinguish a conflict from a crash.
        Assert.True(conflict.IsError == true);
        Assert.StartsWith("conflict:", conflict.Content.OfType<TextContentBlock>().First().Text);
    }

    // Same contract as create_material/create_printer: the key is optional, and without one a
    // retry is a second project. Pinned so the residual risk stays a documented property.
    [Fact]
    public async Task CreateProject_WithoutKey_CreatesASecondProject()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        Dictionary<string, object?> Args() => new() { ["name"] = "Duplicate Project" };

        var first = await client.CallToolAsync("create_project", Args(), cancellationToken: TestContext.Current.CancellationToken);
        var second = await client.CallToolAsync("create_project", Args(), cancellationToken: TestContext.Current.CancellationToken);

        using var firstDoc = JsonDocument.Parse(first.Content.OfType<TextContentBlock>().First().Text);
        using var secondDoc = JsonDocument.Parse(second.Content.OfType<TextContentBlock>().First().Text);
        Assert.NotEqual(
            firstDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid(),
            secondDoc.RootElement.GetProperty("project").GetProperty("projectId").GetGuid());
    }

    [Fact]
    public async Task UpdateProject_ForeignProject_ReturnsNotFound()
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);

        var code = await McpDataWebApplicationFactory.ToolErrorCode(client, "update_project",
            new Dictionary<string, object?>
            {
                ["id"] = McpTestData.ForeignProjectId,
                ["name"] = "hijack",
            });

        Assert.Equal("not_found", code);
    }

    // ---------------------------------------------------------------------
    // Project start / finish dates
    // ---------------------------------------------------------------------

    private static string NewKey() => $"key-{Guid.NewGuid():N}";

    // A project with no prints derives its start date as TODAY, so a finish-only override must
    // be in the future or validation correctly rejects it as an inverted range. Computed rather
    // than hardcoded so these tests do not rot into the past.
    private static string FutureDay(int offsetDays = 0) =>
        DateTime.UtcNow.AddYears(1).AddDays(offsetDays).ToString("yyyy-MM-dd");

    /// <summary>
    /// Calls a tool and parses its single text block as JSON. Driving these through
    /// CallToolAsync rather than the C# methods is the point: it proves DateOnly survives the
    /// real JSON boundary in both directions, which a direct method call would not.
    /// </summary>
    private async Task<JsonDocument> CallJsonAsync(
        string tool, Dictionary<string, object?> args)
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError != true, $"{tool} returned an error: " +
            string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        return JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text);
    }

    private async Task AssertToolErrorAsync(string tool, Dictionary<string, object?> args)
    {
        await using var client = await _factory.ConnectAsync(IntegrationTestSeeder.TestUserOAuthId, ReadWrite);
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError == true, $"expected {tool} to fail but it succeeded");
    }

    [Fact]
    public async Task CreateProject_WithDates_PersistsAndEchoesThem()
    {
        using var doc = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = $"dated-{Guid.NewGuid():N}",
            ["startDate"] = "2026-02-01",
            ["finishDate"] = "2026-03-01",
            ["idempotencyKey"] = NewKey(),
        });

        var project = doc.RootElement.GetProperty("project");
        Assert.Equal("2026-02-01", project.GetProperty("startDate").GetString());
        Assert.Equal("2026-03-01", project.GetProperty("finishDate").GetString());
    }

    [Fact]
    public async Task CreateProject_SameKeyDifferentStartDate_Conflicts()
    {
        var key = NewKey();
        var name = $"conf-{Guid.NewGuid():N}";
        using var _ = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = name, ["startDate"] = "2026-02-01", ["idempotencyKey"] = key,
        });

        await AssertToolErrorAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = name, ["startDate"] = "2026-02-02", ["idempotencyKey"] = key,
        });
    }

    [Fact]
    public async Task CreateProject_SameKeyDifferentFinishDate_Conflicts()
    {
        var key = NewKey();
        var name = $"conf2-{Guid.NewGuid():N}";
        using var _ = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = name, ["finishDate"] = FutureDay(), ["idempotencyKey"] = key,
        });

        await AssertToolErrorAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = name, ["finishDate"] = FutureDay(1), ["idempotencyKey"] = key,
        });
    }

    [Fact]
    public async Task CreateProject_SameKeySameDates_Replays()
    {
        var key = NewKey();
        var name = $"replay-{Guid.NewGuid():N}";
        var args = new Dictionary<string, object?>
        {
            ["name"] = name, ["startDate"] = "2026-02-01", ["idempotencyKey"] = key,
        };

        using var first = await CallJsonAsync("create_project", args);
        using var second = await CallJsonAsync("create_project", args);

        Assert.True(second.RootElement.GetProperty("wasReplayed").GetBoolean());
        Assert.Equal(
            first.RootElement.GetProperty("project").GetProperty("projectId").GetString(),
            second.RootElement.GetProperty("project").GetProperty("projectId").GetString());
    }

    /// <summary>
    /// A date-less create must still replay against a fingerprint recorded BEFORE the date
    /// fields existed. Appending two "absent" flags unconditionally would change the hashed
    /// bytes and turn every such retry into a spurious conflict.
    /// </summary>
    [Fact]
    public async Task CreateProject_WithoutDates_StillReplaysOnTheSameKey()
    {
        var key = NewKey();
        var name = $"legacy-{Guid.NewGuid():N}";
        var args = new Dictionary<string, object?> { ["name"] = name, ["idempotencyKey"] = key };

        using var first = await CallJsonAsync("create_project", args);
        using var second = await CallJsonAsync("create_project", args);

        Assert.True(second.RootElement.GetProperty("wasReplayed").GetBoolean());
    }

    [Fact]
    public async Task UpdateProject_SetsDates()
    {
        using var created = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = $"upd-{Guid.NewGuid():N}", ["idempotencyKey"] = NewKey(),
        });
        var id = created.RootElement.GetProperty("project").GetProperty("projectId").GetGuid();

        using var updated = await CallJsonAsync("update_project", new Dictionary<string, object?>
        {
            ["id"] = id, ["startDate"] = "2026-02-01", ["finishDate"] = "2026-03-01",
        });

        Assert.Equal("2026-02-01", updated.RootElement.GetProperty("startDate").GetString());
        Assert.Equal("2026-03-01", updated.RootElement.GetProperty("finishDate").GetString());
    }

    [Fact]
    public async Task UpdateProject_ClearStartDate_ReturnsToAutomatic()
    {
        using var created = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = $"clr-{Guid.NewGuid():N}", ["startDate"] = "2026-02-01", ["idempotencyKey"] = NewKey(),
        });
        var id = created.RootElement.GetProperty("project").GetProperty("projectId").GetGuid();

        using var updated = await CallJsonAsync("update_project", new Dictionary<string, object?>
        {
            ["id"] = id, ["clearStartDate"] = true,
        });

        // No prints, so it falls back to the project's creation date.
        Assert.Equal(
            DateTime.UtcNow.ToString("yyyy-MM-dd"),
            updated.RootElement.GetProperty("startDate").GetString());
    }

    [Fact]
    public async Task UpdateProject_ClearFinishDate_ReturnsToAutomatic()
    {
        using var created = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = $"clrf-{Guid.NewGuid():N}", ["finishDate"] = FutureDay(), ["idempotencyKey"] = NewKey(),
        });
        var id = created.RootElement.GetProperty("project").GetProperty("projectId").GetGuid();

        using var updated = await CallJsonAsync("update_project", new Dictionary<string, object?>
        {
            ["id"] = id, ["clearFinishDate"] = true,
        });

        // An automatic finish date on a project with no dated prints has no value at all. The
        // serializer omits null members rather than emitting an explicit null, so "absent" is
        // the wire contract a client sees — accept either, reject an actual date.
        var hasFinish = updated.RootElement.TryGetProperty("finishDate", out var finish);
        Assert.True(!hasFinish || finish.ValueKind == JsonValueKind.Null,
            $"expected no finish date, got {(hasFinish ? finish.ToString() : "<absent>")}");
    }

    [Fact]
    public async Task UpdateProject_DateAndItsClearFlagTogether_IsRejected()
    {
        using var created = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = $"rej-{Guid.NewGuid():N}", ["idempotencyKey"] = NewKey(),
        });
        var id = created.RootElement.GetProperty("project").GetProperty("projectId").GetGuid();

        await AssertToolErrorAsync("update_project", new Dictionary<string, object?>
        {
            ["id"] = id, ["startDate"] = "2026-02-01", ["clearStartDate"] = true,
        });
    }

    [Fact]
    public async Task UpdateProject_RejectsInvertedDates()
    {
        using var created = await CallJsonAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = $"inv-{Guid.NewGuid():N}", ["idempotencyKey"] = NewKey(),
        });
        var id = created.RootElement.GetProperty("project").GetProperty("projectId").GetGuid();

        await AssertToolErrorAsync("update_project", new Dictionary<string, object?>
        {
            ["id"] = id, ["startDate"] = "2026-05-01", ["finishDate"] = "2026-04-01",
        });
    }

    [Fact]
    public async Task CreateProject_RejectsInvertedDates()
    {
        await AssertToolErrorAsync("create_project", new Dictionary<string, object?>
        {
            ["name"] = $"cinv-{Guid.NewGuid():N}",
            ["startDate"] = "2026-05-01",
            ["finishDate"] = "2026-04-01",
            ["idempotencyKey"] = NewKey(),
        });
    }

    [Fact]
    public async Task ListProjects_ResolvesDatesFromPrints()
    {
        // The seeded "Rocket Build" project has prints, so its dates must be derived from them
        // rather than reported as its creation date.
        using var page = await CallJsonAsync("list_projects", new Dictionary<string, object?>
        {
            ["search"] = "Rocket",
        });

        var item = page.RootElement.GetProperty("items")[0];
        Assert.Equal(JsonValueKind.String, item.GetProperty("startDate").ValueKind);
        // YYYY-MM-DD, not an ISO instant: a civil date must not acquire a time or an offset.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", item.GetProperty("startDate").GetString()!);
    }
}
