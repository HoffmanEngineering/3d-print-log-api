using System.Text.Json.Nodes;
using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Models;

namespace PrintLogApi.IntegrationTests.Email;

/// <summary>A scriptable campaign: returns <see cref="Due"/>, renders a fixed email or null, can throw.</summary>
public sealed class TestCampaign(string name = "test-campaign") : IEmailCampaign
{
    public string Name { get; } = name;

    public int PreferenceSettingTypeId { get; init; } = EmailSettingTypes.MonthlyRecap;

    public bool CountsTowardFrequencyCap { get; init; } = true;

    public bool ExemptFromFrequencyCap { get; init; }

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromDays(2);

    public List<DueEmail> Due { get; } = [];

    public Exception? ThrowOnFind { get; set; }

    /// <summary>When true, rendering returns null (the email is no longer relevant).</summary>
    public bool RenderNull { get; set; }

    public List<long> RenderedRowIds { get; } = [];

    public Task<IReadOnlyList<DueEmail>> FindDueAsync(DateTimeOffset now, CancellationToken ct)
        => ThrowOnFind is { } ex ? throw ex : Task.FromResult<IReadOnlyList<DueEmail>>(Due.ToList());

    public Task<RenderedEmail?> RenderAsync(EmailOutbox row, CancellationToken ct)
    {
        lock (RenderedRowIds)
        {
            RenderedRowIds.Add(row.Id);
        }

        return Task.FromResult(RenderNull
            ? null
            : new RenderedEmail(
                $"Subject {row.Id}",
                $"<p>Row {row.Id}</p>",
                $"Row {row.Id}",
                PreferenceSettingTypeId,
                new JsonObject { ["row"] = row.Id }));
    }
}
