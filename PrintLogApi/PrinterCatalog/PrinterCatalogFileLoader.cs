using System.Reflection;
using PrintLogApi.PrinterCatalog.Generation;

namespace PrintLogApi.PrinterCatalog;

/// <summary>
/// Reads the committed <c>printer-catalog.json</c>, which the build embeds in the assembly under
/// a pinned logical name (see <c>PrintLogApi.csproj</c>).
/// </summary>
public static class PrinterCatalogFileLoader
{
    public const string ResourceName = "PrintLogApi.PrinterCatalog.printer-catalog.json";

    public static CatalogFile LoadEmbedded() => LoadEmbedded(typeof(PrinterCatalogFileLoader).Assembly, ResourceName);

    /// <summary>The seam for tests: any assembly and resource name.</summary>
    public static CatalogFile LoadEmbedded(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded resource {resourceName} is missing.");
        using var reader = new StreamReader(stream);
        return CatalogFile.Deserialize(reader.ReadToEnd());
    }
}
