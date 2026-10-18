namespace PrintLogApi.Models.DTOs.Sync;

/// <summary>
/// Everything a home-automation client renders, in one response (#234). Built server-side only,
/// so the positional parameters are the contract, and the parameter docs below are the field
/// rules. AGENTS.md ("Integration polling") has the caching reasoning. Assembled per request from
/// cached parts; the printers and spools lists may be shared with the cache, so treat them as
/// read-only.
/// </summary>
/// <param name="SchemaVersion">The shape's version. Additive fields do not change it.</param>
/// <param name="Version">The same opaque value <c>GET /api/sync/version</c> returns.</param>
/// <param name="Printers">Every printer the user owns, active and inactive, by name then id.</param>
/// <param name="Spools">Every material the user owns, all categories, active and archived, by spool number then id.</param>
/// <param name="Account">Small account totals for the current month.</param>
public sealed record SyncSnapshot(
    int SchemaVersion,
    string Version,
    IReadOnlyList<SyncPrinter> Printers,
    IReadOnlyList<SyncSpool> Spools,
    SyncAccountTotals Account)
{
    /// <summary>The current <see cref="SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>One printer: one Home Assistant device, keyed on <see cref="Id"/>.</summary>
/// <param name="Connection">The printer's freshest connector, or null when none is bound to it.</param>
/// <param name="CurrentPrint">
/// The newest print in the Printing state, or null. A print stays Printing until its finish
/// arrives, and one that never does (a Moonraker notifier print whose printer lost power, say)
/// stays here indefinitely. De-emphasize it when <see cref="Connection"/> is <c>stale</c>, and
/// treat one long past its estimated end with suspicion.
/// </param>
/// <param name="LastFinishedPrint">The newest finished print, or null.</param>
/// <param name="Loaded">What is in each slot now, in slot order; unslotted spools last.</param>
/// <param name="PrintTimeSeconds">
/// All-time print time on this printer, the figure <c>GET /api/analytics/printers</c> reports as
/// <c>printTimeSeconds</c> with no date filter: each print's recorded time, else its estimate, else 0.
/// </param>
/// <param name="LastMaintenance">The newest maintenance entry marked done, or null.</param>
/// <param name="OpenMaintenance">Maintenance entries not yet done. Never null.</param>
public sealed record SyncPrinter(
    long Id,
    string? Name,
    string? Make,
    string? Model,
    bool Active,
    int SlotCount,
    SyncConnection? Connection,
    SyncCurrentPrint? CurrentPrint,
    SyncFinishedPrint? LastFinishedPrint,
    IReadOnlyList<SyncLoadedSpool> Loaded,
    long PrintTimeSeconds,
    SyncMaintenance? LastMaintenance,
    SyncOpenMaintenance OpenMaintenance);

/// <summary>A completed maintenance entry.</summary>
/// <param name="Date">The entry's date.</param>
public sealed record SyncMaintenance(Guid Id, DateTimeOffset Date, string? Category, string? Description);

/// <param name="Count">How many entries are not done; 0 when none.</param>
/// <param name="NextDate">The earliest date among them, or null when <see cref="Count"/> is 0.</param>
public sealed record SyncOpenMaintenance(int Count, DateTimeOffset? NextDate);

/// <summary>Connector health, computed per request against the clock.</summary>
/// <param name="Kind">The connector type, such as <c>moonraker</c>.</param>
/// <param name="Status"><c>online</c>, or <c>stale</c> after 15 minutes without a heartbeat.</param>
public sealed record SyncConnection(string Kind, string Status)
{
    public const string Online = "online";
    public const string Stale = "stale";
}

/// <param name="EstimatedEndAt"><see cref="StartedAt"/> plus <see cref="EstimatedSeconds"/>, when both exist.</param>
public sealed record SyncCurrentPrint(
    long Id,
    string? Title,
    DateTimeOffset? StartedAt,
    int? EstimatedSeconds,
    DateTimeOffset? EstimatedEndAt);

/// <param name="Status"><c>success</c>, <c>failed</c>, <c>canceled</c> or <c>partial_success</c>.</param>
/// <param name="FinishedAt">Start plus print time, when both exist; the print stores no finish time of its own.</param>
public sealed record SyncFinishedPrint(
    long Id,
    string? Title,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>A spool in a slot, repeating the matching <see cref="SyncSpool"/> fields a slot sensor needs.</summary>
/// <param name="RemainingGrams">Null for a spool with no initial weight (pending #230).</param>
public sealed record SyncLoadedSpool(
    int? Slot,
    string? SlotLabel,
    Guid SpoolId,
    int? SpoolNumber,
    string? Name,
    string? Material,
    string? ColorHex,
    IReadOnlyList<string> Colors,
    double? RemainingGrams);

/// <param name="Category">The material category: <c>filament</c>, <c>resin</c> or <c>powder</c>.</param>
/// <param name="ColorHex">The first of <see cref="Colors"/>, or null.</param>
/// <param name="Colors">Normalized <c>#RRGGBB</c> (or <c>#RRGGBBAA</c>) values, upper case.</param>
/// <param name="RemainingGrams">The shared remaining figure clamped at 0, or null for a spool with no initial weight (pending #230).</param>
/// <param name="UsedGrams">Taken off the spool by prints and adjustments, clamped at 0.</param>
/// <param name="InitialGrams">The nominal net weight, or null when not tracked.</param>
public sealed record SyncSpool(
    Guid Id,
    int? SpoolNumber,
    string? Name,
    string? Brand,
    string? Material,
    string Category,
    string? ColorName,
    string? ColorHex,
    IReadOnlyList<string> Colors,
    double? RemainingGrams,
    double UsedGrams,
    double? InitialGrams,
    string? StorageLocation,
    bool Active);

/// <param name="Month">The calendar month counted, <c>yyyy-MM</c>, in <see cref="TimeZone"/>.</param>
/// <param name="TimeZone">The zone the month was taken in, as requested.</param>
/// <param name="PrintCount">Prints started this month.</param>
/// <param name="Grams">What those prints' usage rows took, in grams.</param>
public sealed record SyncAccountTotals(string Month, string TimeZone, int PrintCount, double Grams);
