using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// The Markdown diff between two catalog files, for the body of the monthly refresh PR. One line
/// per model, each section capped so a large upstream change stays readable.
/// </summary>
public static class CatalogSummary
{
    /// <summary>The most model lines one section shows before "and N more".</summary>
    public const int SectionCap = 100;

    // Provenance, not facts a reviewer needs to read; a change here always comes with a real one.
    private static readonly HashSet<string> IgnoredFields = ["id", "sourceKeys", "sources"];

    /// <param name="hiddenIds">Ids hidden by an override, which are not gone from the sources.</param>
    public static string Build(CatalogFile? previous, CatalogFile current, IReadOnlySet<string> hiddenIds)
    {
        var before = (previous?.Models ?? []).ToDictionary(m => m.Id);
        var after = current.Models.ToDictionary(m => m.Id);
        var mergedInto = current.Reserved.Where(r => r.MergedInto is not null).ToDictionary(r => r.Id, r => r.MergedInto!);

        var added = current.Models.Where(m => !before.ContainsKey(m.Id)).Select(m => $"Added: {Name(m)}").ToList();
        var changed = new List<string>();
        foreach (var model in current.Models.Where(m => before.ContainsKey(m.Id)))
        {
            var fields = ChangedFields(before[model.Id], model);
            if (fields.Count > 0)
            {
                changed.Add($"Changed: {Name(model)} ({string.Join(", ", fields)})");
            }
        }
        var gone = (previous?.Models ?? []).Where(m => !after.ContainsKey(m.Id)).ToList();
        var merged = gone.Where(m => mergedInto.ContainsKey(m.Id)).Select(m => $"Merged: {Name(m)} into {mergedInto[m.Id]}").ToList();
        var hidden = gone.Where(m => !mergedInto.ContainsKey(m.Id) && hiddenIds.Contains(m.Id)).Select(m => $"Hidden: {Name(m)}").ToList();
        var retired = gone.Where(m => !mergedInto.ContainsKey(m.Id) && !hiddenIds.Contains(m.Id)).Select(m => $"Retired: {Name(m)}").ToList();

        var text = new StringBuilder();
        if (added.Count + changed.Count + retired.Count + hidden.Count + merged.Count == 0)
        {
            return "No changes to the printer models.\n";
        }
        Section(text, "Added", added);
        Section(text, "Changed", changed);
        Section(text, "Retired (gone from the sources)", retired);
        Section(text, "Hidden by an override", hidden);
        Section(text, "Merged", merged);
        return text.ToString();
    }

    private static string Name(CatalogModel m) => $"{m.Make} {m.Model}".Trim();

    private static void Section(StringBuilder text, string title, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }
        text.Append("### ").Append(title).Append(" (").Append(lines.Count).Append(")\n\n");
        foreach (var line in lines.Take(SectionCap))
        {
            text.Append("- ").Append(line).Append('\n');
        }
        if (lines.Count > SectionCap)
        {
            text.Append("- and ").Append(lines.Count - SectionCap).Append(" more\n");
        }
        text.Append('\n');
    }

    private static List<string> ChangedFields(CatalogModel was, CatalogModel now)
    {
        var a = Fields(was);
        var b = Fields(now);
        return a.Keys.Union(b.Keys).Order(StringComparer.Ordinal)
            .Where(k => !IgnoredFields.Contains(k))
            .Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .ToList();
    }

    private static Dictionary<string, string> Fields(CatalogModel model) =>
        ((JsonObject)JsonSerializer.SerializeToNode(model, CatalogFile.JsonOptions)!)
        .ToDictionary(p => p.Key, p => p.Value?.ToJsonString() ?? "null");
}
