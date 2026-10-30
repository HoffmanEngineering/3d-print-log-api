using PrintLogApi.Models;

namespace PrintLogApi.Services.Sync;

/// <summary>The snapshot's wire spelling of a print's source, and its web link.</summary>
internal static class SyncPrintSource
{
    /// <summary>Snake case, written out because <c>octoprint</c> is not a mechanical conversion of OctoPrint.</summary>
    public static string Name(PrintSource source) => source switch
    {
        PrintSource.Web => "web",
        PrintSource.SlicerPlugin => "slicer_plugin",
        PrintSource.OctoPrint => "octoprint",
        PrintSource.Moonraker => "moonraker",
        PrintSource.Mcp => "mcp",
        PrintSource.ApiKey => "api_key",
        PrintSource.PrusaLink => "prusalink",
        PrintSource.Bambu => "bambu",
        PrintSource.Sdcp => "sdcp",
        PrintSource.Duet => "duet",
        PrintSource.FlashForge => "flashforge",
        PrintSource.Anycubic => "anycubic",
        _ => "unknown",
    };

    /// <summary>The webhook rule: the web base without a trailing slash, null when none is configured.</summary>
    public static string? Url(string? webBase, long printId) =>
        string.IsNullOrEmpty(webBase) ? null : $"{webBase}/prints/{printId}";
}
