namespace PrintLogApi.Models;

/// <summary>
/// How a print reached the API. Set on the server only, never taken from a request body.
/// Rows created before the column existed stay <see cref="Unknown"/>.
/// </summary>
public enum PrintSource
{
    Unknown = 0,
    Web = 1,
    SlicerPlugin = 2,
    OctoPrint = 3,
    Moonraker = 4,
    Mcp = 5,
    ApiKey = 6,

    // printlog-bridge adapters beyond Moonraker (printlog-bridge#82), taken from the kind of the
    // connection a print links to. A printlog-bridge `octoprint` connection reuses OctoPrint.
    PrusaLink = 7,
    Bambu = 8,

    /// <summary>Elegoo printers, over the SDCP protocol.</summary>
    Sdcp = 9,

    /// <summary>Duet printers, over RepRapFirmware's HTTP API (printlog-bridge#89).</summary>
    Duet = 10,

    /// <summary>FlashForge Adventurer 5M, 5M Pro and AD5X, over their LAN HTTP API (printlog-bridge#88).</summary>
    FlashForge = 11,

    /// <summary>Anycubic printers in LAN mode, FDM and resin (printlog-bridge#96, #97).</summary>
    Anycubic = 12,
}
