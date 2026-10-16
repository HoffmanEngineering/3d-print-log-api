using Microsoft.Extensions.Options;

namespace PrintLogApi.SpoolTags;

/// <summary>
/// How the native app derives MIFARE Classic sector keys, per tag format. The parameters are secrets
/// in everything but name, so they come from configuration (untracked settings, App Service) and never from the repo.
/// </summary>
public sealed class TagReaderOptions
{
    public const string SectionName = "TagReader";

    public List<TagReaderFormat> Formats { get; set; } = [];
}

public sealed class TagReaderFormat
{
    public string? Id { get; set; }
    public string? Scheme { get; set; }
    public Dictionary<string, string>? Params { get; set; }

    private static readonly string[] KnownParams = ["saltHex", "infoHex", "labelPrefix", "sectors"];

    /// <summary>
    /// The params with case-insensitive lookup. Keys that match a known param in any case are
    /// renamed to its canonical spelling, since the native app reads those names exactly (environment
    /// variables and some config sources change the case).
    /// </summary>
    public Dictionary<string, string> NormalizedParams()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Params ?? [])
        {
            result[KnownParams.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key] = value;
        }

        return result;
    }
}

/// <summary>Fails startup on a reader config the app could not use, naming the offending format.</summary>
public sealed class TagReaderOptionsValidator : IValidateOptions<TagReaderOptions>
{
    public const string HmacSha256Sector = "hmac-sha256-sector";
    public const string HkdfSha256Sectors = "hkdf-sha256-sectors";

    private const string SectorsParam = "sectors";

    public ValidateOptionsResult Validate(string? name, TagReaderOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.Formats.Count; i++)
        {
            var format = options.Formats[i];
            if (string.IsNullOrWhiteSpace(format.Id))
            {
                failures.Add($"TagReader:Formats:{i} has no Id.");
                continue;
            }

            var id = format.Id;
            if (!seen.Add(id))
            {
                failures.Add($"TagReader format '{id}' is configured more than once (ids compare case-insensitively).");
            }

            var values = format.NormalizedParams();
            switch (format.Scheme)
            {
                case HmacSha256Sector:
                    RequireHex(failures, id, values, "saltHex");
                    RequirePresent(failures, id, values, "labelPrefix");
                    break;
                case HkdfSha256Sectors:
                    RequireHex(failures, id, values, "saltHex");
                    RequireHex(failures, id, values, "infoHex");
                    break;
                default:
                    failures.Add($"TagReader format '{id}' has unknown scheme '{format.Scheme}'.");
                    continue;
            }

            if (values.TryGetValue(SectorsParam, out var sectors)
                && !(int.TryParse(sectors, out var count) && count is >= 1 and <= 16))
            {
                failures.Add($"TagReader format '{id}' has sectors '{sectors}'; it must be a number from 1 to 16.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePresent(List<string> failures, string id, Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
        {
            failures.Add($"TagReader format '{id}' is missing param '{key}'.");
        }
    }

    private static void RequireHex(List<string> failures, string id, Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
        {
            failures.Add($"TagReader format '{id}' is missing param '{key}'.");
            return;
        }

        if (value.Length % 2 != 0 || !value.All(Uri.IsHexDigit))
        {
            failures.Add($"TagReader format '{id}' param '{key}' must be non-empty, even-length hex.");
        }
    }
}
