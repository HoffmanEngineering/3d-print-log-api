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
}
