using System.Diagnostics.CodeAnalysis;

namespace PrintLogApi.Services;

/// <summary>
/// Resolves a user-supplied time zone id. Shared by analytics filters and achievement date
/// metrics so both read a zone the same way.
/// </summary>
public static class TimeZoneResolver
{
    /// <summary>
    /// Resolves an IANA id, falling back to the Windows id on hosts without ICU IANA support.
    /// </summary>
    public static bool TryResolve(string? ianaOrWindowsId, [MaybeNullWhen(false)] out TimeZoneInfo zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(ianaOrWindowsId)) return false;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(ianaOrWindowsId); return true; }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { return false; }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaOrWindowsId, out var windowsId))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(windowsId); return true; }
            catch (TimeZoneNotFoundException) { }
        }
        return false;
    }

    /// <summary>The resolved zone, or UTC when the id is missing or unrecognized. Never throws.</summary>
    public static TimeZoneInfo ResolveOrUtc(string? id) => TryResolve(id, out var zone) ? zone : TimeZoneInfo.Utc;
}
