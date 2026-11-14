using Microsoft.EntityFrameworkCore;

namespace PrintLogApi.SpoolTags.Labels;

/// <summary>
/// The catalog's manufacturers and, per manufacturer, its entry names: the words a label read is
/// matched against. About 10,000 short strings, held in a field rather than <c>IMemoryCache</c>
/// (like <c>DocsCatalog</c>) and rebuilt when the catalog's live row count or newest import time
/// changes, which costs one aggregate query per read.
/// </summary>
public sealed class LabelVocabulary(IServiceScopeFactory scopeFactory)
{
    public sealed record Snapshot(
        IReadOnlyList<string> Brands,
        IReadOnlyDictionary<string, IReadOnlyList<string>> NamesByBrand);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (int Count, DateTime? Newest) _version;
    private Snapshot _snapshot = new([], new Dictionary<string, IReadOnlyList<string>>());

    public async Task<Snapshot> GetAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var live = db.FilamentCatalogEntries.AsNoTracking().Where(e => e.RemovedAt == null);
        var version = (await live.CountAsync(ct), await live.MaxAsync(e => (DateTime?)e.ImportedAt, ct));
        if (version == _version)
        {
            return _snapshot;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (version != _version)
            {
                var rows = await live.Select(e => new { e.Manufacturer, e.Name }).ToListAsync(ct);
                var byBrand = rows
                    .GroupBy(r => r.Manufacturer, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => (IReadOnlyList<string>)g.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        StringComparer.OrdinalIgnoreCase);
                _snapshot = new Snapshot(byBrand.Keys.ToList(), byBrand);
                _version = version;
            }
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }
}
