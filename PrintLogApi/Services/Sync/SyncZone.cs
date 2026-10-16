namespace PrintLogApi.Services.Sync;

/// <summary>
/// A time zone under one canonical id (#234 review). Aliases (<c>US/Eastern</c>, the Windows id
/// <c>Eastern Standard Time</c>, <c>America/New_York</c>) would otherwise each get their own cache
/// entry, tag and echoed name for the same month boundaries.
/// </summary>
public sealed record SyncZone(string Id, TimeZoneInfo Zone)
{
    public static readonly SyncZone Utc = new("UTC", TimeZoneInfo.Utc);

    /// <summary>
    /// Resolves <paramref name="id"/> (IANA or Windows) and canonicalizes it through its Windows
    /// zone to that zone's primary IANA id, as CLDR's <c>windowsZones</c> maps it. Zones that share
    /// a Windows zone share its current rules, so they share every month boundary this is used
    /// for; the echoed id can therefore be a sibling (<c>Europe/Paris</c> reads back as
    /// <c>Europe/Berlin</c>) or CLDR's spelling (<c>Asia/Kolkata</c> as <c>Asia/Calcutta</c>).
    /// Every UTC alias reads back as <c>UTC</c>.
    /// </summary>
    public static bool TryResolve(string? id, out SyncZone zone)
    {
        zone = Utc;
        if (!TimeZoneResolver.TryResolve(id?.Trim(), out var resolved))
        {
            return false;
        }

        var windowsId = resolved.HasIanaId
            ? (TimeZoneInfo.TryConvertIanaIdToWindowsId(resolved.Id, out var w) ? w : null)
            : resolved.Id;
        var canonical = windowsId is not null && TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId, out var iana)
            ? iana
            : resolved.Id;

        if (canonical is "Etc/UTC" or "Etc/GMT" or "Etc/UCT" or "Etc/Universal" or "Etc/Zulu" or "UTC" or "GMT"
            || resolved.Id is "UTC" or "Etc/UTC")
        {
            return true;
        }

        zone = new SyncZone(canonical, TimeZoneResolver.TryResolve(canonical, out var canonicalZone) ? canonicalZone : resolved);
        return true;
    }
}
