using System.Globalization;

namespace PrintLogApi.Services.Spoolman;

/// <summary>A query string Spoolman would reject with 400; the message is Spoolman's own wording where it has one.</summary>
public sealed class SpoolmanQueryException(string message) : Exception(message);

/// <summary>
/// Spoolman's list semantics (#177), applied in memory to a user's cached inventory: string
/// filters, id filters, <c>sort=field:asc,...</c> and <c>limit</c>/<c>offset</c>. Each rule is
/// Spoolman v0.27.0's (<c>spoolman/database/utils.py</c>), so a client gets the same answer from
/// either server.
/// </summary>
public static class SpoolmanQuery
{
    /// <summary>
    /// Spoolman's string filter. Comma-separated terms, any of which may match. An empty term
    /// matches a missing value; a term in double quotes is an exact match (and may contain commas);
    /// anything else is a case-insensitive substring match. A null filter matches everything.
    /// </summary>
    public static bool MatchesTerms(string? value, string? filter)
    {
        if (filter is null)
        {
            return true;
        }

        foreach (var term in SplitTerms(filter))
        {
            if (term.Length == 0)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return true;
                }
            }
            else if (term.Length >= 2 && term[0] == '"' && term[^1] == '"')
            {
                if (string.Equals(value, term[1..^1], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Spoolman's <c>split_filter_terms</c>: splits on commas, keeping a quoted term whole. A quote
    /// that does not close a term is not special.
    /// </summary>
    public static IReadOnlyList<string> SplitTerms(string value)
    {
        var terms = new List<string>();
        var pos = 0;
        while (true)
        {
            if (pos < value.Length && value[pos] == '"')
            {
                var end = value.IndexOf("\",", pos + 1, StringComparison.Ordinal);
                if (end != -1)
                {
                    terms.Add(value[pos..(end + 1)]);
                    pos = end + 2;
                    continue;
                }

                if (pos < value.Length - 1 && value.EndsWith('"'))
                {
                    terms.Add(value[pos..]);
                    return terms;
                }
            }

            var comma = value.IndexOf(',', pos);
            if (comma == -1)
            {
                terms.Add(value[pos..]);
                return terms;
            }

            terms.Add(value[pos..comma]);
            pos = comma + 1;
        }
    }

    /// <summary>A comma-separated id list (<c>filament.id=1,2</c>), or null when the parameter is absent.</summary>
    public static IReadOnlySet<int>? ParseIds(string? value, string parameter)
    {
        if (value is null)
        {
            return null;
        }

        var ids = new HashSet<int>();
        foreach (var part in value.Split(','))
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                throw new SpoolmanQueryException($"Invalid value '{part}' for '{parameter}', expected a comma-separated list of integers.");
            }
            ids.Add(id);
        }

        return ids;
    }

    /// <summary>Spoolman's <c>parse_sort</c>: <c>field:asc,field:desc</c>, in priority order.</summary>
    public static IReadOnlyList<(string Field, bool Descending)> ParseSort(string? sort)
    {
        var result = new List<(string, bool)>();
        if (sort is null)
        {
            return result;
        }

        foreach (var raw in sort.Split(','))
        {
            var item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            var colon = item.IndexOf(':');
            if (colon < 0)
            {
                throw new SpoolmanQueryException($"Invalid sort '{item}', expected the form 'field:asc' or 'field:desc'.");
            }

            var field = item[..colon];
            var direction = item[(colon + 1)..].Trim();
            if (field.Length == 0)
            {
                throw new SpoolmanQueryException($"Invalid sort '{item}', no field name was given.");
            }

            var descending = direction.ToUpperInvariant() switch
            {
                "ASC" => false,
                "DESC" => true,
                _ => throw new SpoolmanQueryException($"Invalid sort direction '{direction}' for field '{field}', expected 'asc' or 'desc'."),
            };
            result.Add((field, descending));
        }

        return result;
    }

    /// <summary>
    /// Sorts by each requested field in turn, nulls last whichever way the list points (Spoolman's
    /// <c>order_by_clauses</c>), then by id so the order is total. An unknown field is a 400.
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> items,
        string? sort,
        Func<string, Func<T, IComparable?>?> fieldFor,
        Func<T, int> id)
    {
        var keys = ParseSort(sort)
            .Select(s => (Key: fieldFor(s.Field) ?? throw new SpoolmanQueryException($"Unknown sort field '{s.Field}'."), s.Descending))
            .ToList();

        IOrderedEnumerable<T>? ordered = null;
        foreach (var (key, descending) in keys)
        {
            var comparer = new NullsLastComparer(descending);
            ordered = ordered is null ? items.OrderBy(key, comparer) : ordered.ThenBy(key, comparer);
        }

        return (ordered is null ? items.OrderBy(id) : ordered.ThenBy(id)).ToList();
    }

    /// <summary>Spoolman's paging: <c>offset</c> applies only together with <c>limit</c>.</summary>
    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int? limit, int offset)
    {
        if (limit is null)
        {
            return items;
        }

        if (limit < 0 || offset < 0)
        {
            throw new SpoolmanQueryException("limit and offset must not be negative.");
        }

        return items.Skip(offset).Take(limit.Value).ToList();
    }

    /// <summary>The sort keys Spoolman accepts on <c>/spool</c> that this server can answer.</summary>
    public static Func<SpoolmanSpool, IComparable?>? SpoolField(string field) => field switch
    {
        "id" => s => s.Id,
        "registered" => s => s.Registered,
        "first_used" => s => s.FirstUsed,
        "last_used" => s => s.LastUsed,
        "price" => s => s.Price ?? s.Filament.Price,
        "remaining_weight" => s => s.RemainingWeight,
        "initial_weight" => s => s.InitialWeight,
        "spool_weight" => s => s.SpoolWeight,
        "used_weight" => s => s.UsedWeight,
        "remaining_length" => s => s.RemainingLength,
        "used_length" => s => s.UsedLength,
        "location" => s => Text(s.Location),
        "lot_nr" => s => Text(s.LotNr),
        "comment" => s => Text(s.Comment),
        "archived" => s => s.Archived,
        "filament.combined_name" => s => Text($"{s.Filament.Vendor?.Name} {s.Filament.Name}".Trim()),
        _ when field.StartsWith("filament.", StringComparison.Ordinal) && FilamentField(field["filament.".Length..]) is { } inner
            => s => inner(s.Filament),
        _ => null,
    };

    /// <summary>The sort keys Spoolman accepts on <c>/filament</c> that this server can answer.</summary>
    public static Func<SpoolmanFilament, IComparable?>? FilamentField(string field) => field switch
    {
        "id" => f => f.Id,
        "registered" => f => f.Registered,
        "name" => f => Text(f.Name),
        "material" => f => Text(f.Material),
        "price" => f => f.Price,
        "density" => f => f.Density,
        "diameter" => f => f.Diameter,
        "weight" => f => f.Weight,
        "spool_weight" => f => f.SpoolWeight,
        "article_number" => f => Text(f.ArticleNumber),
        "comment" => f => Text(f.Comment),
        "settings_extruder_temp" => f => f.SettingsExtruderTemp,
        "settings_bed_temp" => f => f.SettingsBedTemp,
        "color_hex" => f => Text(f.ColorHex),
        _ when field.StartsWith("vendor.", StringComparison.Ordinal) && VendorField(field["vendor.".Length..]) is { } inner
            => f => f.Vendor is null ? null : inner(f.Vendor),
        _ => null,
    };

    /// <summary>The sort keys Spoolman accepts on <c>/vendor</c> that this server can answer.</summary>
    public static Func<SpoolmanVendor, IComparable?>? VendorField(string field) => field switch
    {
        "id" => v => v.Id,
        "registered" => v => v.Registered,
        "name" => v => Text(v.Name),
        "comment" => v => Text(v.Comment),
        "empty_spool_weight" => v => v.EmptySpoolWeight,
        _ => null,
    };

    /// <summary>Text sorts case-insensitively; a database collation usually would.</summary>
    private static IComparable? Text(string? value) => value?.ToUpperInvariant();

    private sealed class NullsLastComparer(bool descending) : IComparer<IComparable?>
    {
        public int Compare(IComparable? x, IComparable? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : 1) : -1;
            }

            var result = x.CompareTo(y);
            return descending ? -result : result;
        }
    }
}
