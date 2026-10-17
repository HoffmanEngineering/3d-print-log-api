using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace PrintLogApi.SpoolTags.Catalog;

/// <summary>One filament as SpoolmanDB lists it. Only the fields the importer reads are declared; the rest are ignored.</summary>
public sealed record SpoolmanEntry(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("manufacturer")] string? Manufacturer,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("material")] string? Material,
    [property: JsonPropertyName("density")] double? Density,
    [property: JsonPropertyName("weight")] double? Weight,
    [property: JsonPropertyName("spool_weight")] double? SpoolWeight,
    [property: JsonPropertyName("spool_type")] string? SpoolType,
    [property: JsonPropertyName("diameter")] double? Diameter,
    [property: JsonPropertyName("color_hex")] string? ColorHex,
    [property: JsonPropertyName("color_hexes")] string[]? ColorHexes,
    [property: JsonPropertyName("extruder_temp")] double? ExtruderTemp,
    [property: JsonPropertyName("extruder_temp_range")] double[]? ExtruderTempRange,
    [property: JsonPropertyName("bed_temp")] double? BedTemp,
    [property: JsonPropertyName("bed_temp_range")] double[]? BedTempRange,
    [property: JsonPropertyName("finish")] string? Finish,
    [property: JsonPropertyName("pattern")] string? Pattern,
    [property: JsonPropertyName("translucent")] bool? Translucent,
    [property: JsonPropertyName("glow")] bool? Glow);

public sealed record SpoolmanFetch(bool NotModified, IReadOnlyList<SpoolmanEntry>? Entries, string? ETag);

public sealed class CatalogFetchException : Exception
{
    public CatalogFetchException(string message) : base(message)
    {
    }

    public CatalogFetchException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Downloads the SpoolmanDB catalog (or reads <see cref="SpoolTagOptions.CatalogFilePath"/>), with a conditional GET and a size cap.</summary>
public sealed class SpoolmanDbClient(HttpClient http, IOptions<SpoolTagOptions> options)
{
    public const string HttpClientName = "SpoolmanDb";

    public async Task<SpoolmanFetch> FetchAsync(string? etag, CancellationToken ct)
    {
        var settings = options.Value;
        if (!string.IsNullOrWhiteSpace(settings.CatalogFilePath))
        {
            return await ReadFileAsync(settings, ct);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, settings.CatalogUrl);
        if (!string.IsNullOrEmpty(etag) && EntityTagHeaderValue.TryParse(etag, out var parsed))
        {
            request.Headers.IfNoneMatch.Add(parsed);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CatalogFetchException("The SpoolmanDB request failed.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CatalogFetchException("The SpoolmanDB request timed out.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new SpoolmanFetch(true, null, etag);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CatalogFetchException($"SpoolmanDB answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > settings.MaxCatalogBytes)
            {
                throw new CatalogFetchException($"The SpoolmanDB catalog is larger than {settings.MaxCatalogBytes} bytes.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var entries = await ParseAsync(body, settings.MaxCatalogBytes, ct);
            return new SpoolmanFetch(false, entries, response.Headers.ETag?.ToString());
        }
    }

    private static async Task<SpoolmanFetch> ReadFileAsync(SpoolTagOptions settings, CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(settings.CatalogFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new SpoolmanFetch(false, await ParseAsync(file, settings.MaxCatalogBytes, ct), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CatalogFetchException("The catalog file could not be read.", ex);
        }
    }

    private static async Task<IReadOnlyList<SpoolmanEntry>> ParseAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new CatalogFetchException($"The SpoolmanDB catalog is larger than {maxBytes} bytes.");
            }
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        try
        {
            var entries = await JsonSerializer.DeserializeAsync<List<SpoolmanEntry>>(buffer, cancellationToken: ct);
            return entries ?? throw new CatalogFetchException("The SpoolmanDB catalog is empty.");
        }
        catch (JsonException ex)
        {
            throw new CatalogFetchException("The SpoolmanDB catalog is not valid JSON.", ex);
        }
    }
}
