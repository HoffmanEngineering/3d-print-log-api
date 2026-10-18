using PrintLogApi.PrinterCatalog.Generation;

namespace PrintLogApi.PrinterCatalog;

/// <summary>
/// Reads the committed <c>printer-catalog.json</c>, which the build embeds in the assembly under
/// a pinned logical name (see <c>PrintLogApi.csproj</c>).
/// </summary>
public static class PrinterCatalogFileLoader
{
    public const string ResourceName = "PrintLogApi.PrinterCatalog.printer-catalog.json";

    public static CatalogFile LoadEmbedded()
    {
        using var stream = typeof(PrinterCatalogFileLoader).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return CatalogFile.Deserialize(reader.ReadToEnd());
    }
}
