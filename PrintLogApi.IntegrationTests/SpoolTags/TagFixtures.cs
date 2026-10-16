using System.Text.Json;
using PrintLogApi.Models.DTOs.SpoolTags;
using PrintLogApi.SpoolTags;

namespace PrintLogApi.IntegrationTests.SpoolTags;

/// <summary>Reads the raw tag dumps under tests-fixtures/spool-tags (request bodies, signature region zeroed).</summary>
public static class TagFixtures
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static TagReadRequest Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tests-fixtures", "spool-tags", name + ".json");
        return JsonSerializer.Deserialize<TagReadRequest>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"Fixture {name} is empty.");
    }

    public static TagMemory Memory(string name)
    {
        return TagMemory.Parse(Load(name).Memory) ?? throw new InvalidOperationException($"Fixture {name} has no memory.");
    }
}
