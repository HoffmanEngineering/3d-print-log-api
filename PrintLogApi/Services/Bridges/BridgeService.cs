using Microsoft.EntityFrameworkCore;
using PrintLogApi.Extensions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Bridges;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Services.AgentReleases;

namespace PrintLogApi.Services.Bridges;

/// <summary>A refusal with an HTTP status and a machine-readable code, for the controller to answer.</summary>
public sealed class BridgeProblemException(int status, string code, string detail, long? bridgeId = null) : Exception(detail)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    /// <summary>The other bridge involved, for <c>printer_connected_elsewhere</c>.</summary>
    public long? BridgeId { get; } = bridgeId;
}

/// <summary>The device grant behind a check-in was revoked while the check-in ran.</summary>
public sealed class BridgeGrantRevokedException : Exception;

/// <summary>A check-in's result: the desired printers, and whether the bridge was registered just now.</summary>
public sealed record BridgeCheckInResult(DesiredPrintersDto Desired, bool Registered);

public interface IBridgeService
{
    /// <summary>
    /// Registers or refreshes the bridge behind <paramref name="grantId"/> from a validated
    /// report and returns its desired printers. Never creates a desired printer. Throws
    /// <see cref="BridgeGrantRevokedException"/> when the grant is gone.
    /// </summary>
    Task<BridgeCheckInResult> CheckInAsync(long grantId, ValidCheckIn report, CancellationToken ct);

    /// <summary>
    /// The bridge's desired-list version, or null before its first check-in. One small read,
    /// plus a write of its last-seen time when that is older than <see cref="BridgeService.PollSeenResolution"/>,
    /// so a bridge whose check-ins a backfill holds back still shows as online while it polls.
    /// </summary>
    Task<string?> PollDesiredVersionAsync(long grantId, CancellationToken ct);

    /// <summary>The bridge's desired printers, or null before its first check-in.</summary>
    Task<DesiredPrintersDto?> GetDesiredAsync(long grantId, CancellationToken ct);

    Task<IReadOnlyList<BridgeDto>> ListAsync(long userId, CancellationToken ct);

    Task<BridgeDto?> GetAsync(long userId, long bridgeId, CancellationToken ct);

    /// <summary>Null keeps a field, empty clears it. Returns false when the user has no such bridge.</summary>
    Task<bool> UpdateAsync(long userId, long bridgeId, string? name, string? location, CancellationToken ct);

    Task<IReadOnlyList<DiscoveredPrinterDto>?> ListDiscoveredAsync(long userId, long bridgeId, CancellationToken ct);

    Task<IReadOnlyList<BridgePrinterDto>?> ListPrintersAsync(long userId, long bridgeId, CancellationToken ct);

    /// <summary>
    /// Adds a desired printer: the only way one is ever created. Null when the user has no such
    /// bridge; <see cref="BridgeProblemException"/> for a refusal.
    /// </summary>
    Task<BridgePrinterDto?> ConnectAsync(long userId, long bridgeId, ConnectBridgePrinterDto body, CancellationToken ct);

    Task<bool> DisconnectAsync(long userId, long bridgeId, long entryId, CancellationToken ct);

    /// <summary>Ignores or stops ignoring a discovered printer. False when the bridge or printer isn't the user's.</summary>
    Task<bool> SetIgnoredAsync(long userId, long bridgeId, long discoveredId, bool ignored, CancellationToken ct);

    /// <summary>Asks the bridge for an active sweep; at most one new request a minute. Null when the user has no such bridge.</summary>
    Task<DateTimeOffset?> RequestScanAsync(long userId, long bridgeId, CancellationToken ct);
}

/// <summary>
/// Bridges, what they find and what they are told to watch (printlog-bridge#100, #103). Nothing
/// here touches the user's cache version: no cached response reads bridges, and a check-in every
/// few minutes must not flush the summaries.
/// </summary>
public sealed class BridgeService(PrintLogContext context, IAgentReleaseCatalog agentReleases, TimeProvider clock) : IBridgeService
{
    /// <summary>A second "Scan now" within this window is the same request.</summary>
    public static readonly TimeSpan ScanRequestWindow = TimeSpan.FromMinutes(1);

    /// <summary>A poll refreshes the bridge's last-seen time at most this often.</summary>
    public static readonly TimeSpan PollSeenResolution = TimeSpan.FromMinutes(2);

    public async Task<BridgeCheckInResult> CheckInAsync(long grantId, ValidCheckIn report, CancellationToken ct)
    {
        try
        {
            return await CheckInOnceAsync(grantId, report, ct);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            if (!await context.DeviceGrants.AnyAsync(g => g.Id == grantId, ct))
            {
                throw new BridgeGrantRevokedException();
            }

            // Two check-ins of one bridge raced (its first, or the same new printer): the other
            // committed, so this one now updates what it inserted.
            return await CheckInOnceAsync(grantId, report, ct);
        }
    }

    private async Task<BridgeCheckInResult> CheckInOnceAsync(long grantId, ValidCheckIn report, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var bridge = await context.Bridges.SingleOrDefaultAsync(b => b.Id == grantId, ct);
        var registered = bridge is null;
        if (bridge is null)
        {
            bridge = new Bridge { Id = grantId, CreatedDate = now, DesiredRevision = Guid.NewGuid() };
            context.Bridges.Add(bridge);
        }

        bridge.LastSeenAt = now;
        bridge.AgentVersion = report.AgentVersion ?? bridge.AgentVersion;
        bridge.Mode = report.Mode ?? bridge.Mode;
        bridge.HostName = report.HostName ?? bridge.HostName;
        if (report.Networks is { } networks)
        {
            bridge.Networks = networks.Count == 0 ? null : string.Join(' ', networks);
        }

        if (report.LastSweepAt is { } swept)
        {
            bridge.LastSweepAt = swept.UtcDateTime < now ? swept.UtcDateTime : now;
        }

        if (report.Discovered is { } discovered)
        {
            await ReplaceDiscoveredAsync(bridge.Id, discovered, now, ct);
        }

        await context.SaveChangesAsync(ct);
        return new BridgeCheckInResult(await DesiredAsync(bridge, ct), registered);
    }

    /// <summary>
    /// The report replaces the list. A printer still reported keeps its row (id, first seen),
    /// matched by identity, so the app's links to it survive an address change.
    /// </summary>
    private async Task ReplaceDiscoveredAsync(long bridgeId, IReadOnlyList<ValidDiscovery> discovered, DateTime now, CancellationToken ct)
    {
        var existing = (await context.BridgeDiscoveredPrinters.Where(d => d.BridgeId == bridgeId).ToListAsync(ct))
            .ToDictionary(d => d.IdentityKey, StringComparer.OrdinalIgnoreCase);

        foreach (var found in discovered)
        {
            if (!existing.Remove(found.IdentityKey, out var row))
            {
                row = new BridgeDiscoveredPrinter { BridgeId = bridgeId, IdentityKey = found.IdentityKey, FirstSeenAt = now };
                context.BridgeDiscoveredPrinters.Add(row);
            }

            row.Kind = found.Kind;
            row.Address = found.Address;
            row.Port = found.Port;
            row.Model = found.Model;
            row.Name = found.Name;
            row.Serial = found.Serial;
            row.MainboardId = found.MainboardId;
            row.Macs = found.Macs.Count == 0 ? null : string.Join(' ', found.Macs);
            row.Configured = found.Configured;
            row.LastSeenAt = now;
        }

        context.BridgeDiscoveredPrinters.RemoveRange(existing.Values);
    }

    public async Task<string?> PollDesiredVersionAsync(long grantId, CancellationToken ct)
    {
        var stamp = await context.Bridges.AsNoTracking()
            .Where(b => b.Id == grantId)
            .Select(b => new { b.DesiredRevision, b.LastSeenAt })
            .SingleOrDefaultAsync(ct);
        if (stamp is null)
        {
            return null;
        }

        // Liveness only: no cache version, and conditional so concurrent polls write once.
        var now = clock.GetUtcNow().UtcDateTime;
        var staleBefore = now - PollSeenResolution;
        if (stamp.LastSeenAt < staleBefore)
        {
            await context.Bridges
                .Where(b => b.Id == grantId && b.LastSeenAt < staleBefore)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.LastSeenAt, now), ct);
        }

        return Version(stamp.DesiredRevision);
    }

    public async Task<DesiredPrintersDto?> GetDesiredAsync(long grantId, CancellationToken ct)
    {
        var bridge = await context.Bridges.AsNoTracking().SingleOrDefaultAsync(b => b.Id == grantId, ct);
        return bridge is null ? null : await DesiredAsync(bridge, ct);
    }

    private async Task<DesiredPrintersDto> DesiredAsync(Bridge bridge, CancellationToken ct) => new()
    {
        Version = Version(bridge.DesiredRevision),
        ScanRequestedAt = Utc(bridge.ScanRequestedAt),
        Printers = await PrintersOf(bridge.Id, ct),
    };

    private static string Version(Guid revision) => revision.ToString("N");

    private async Task<List<BridgePrinterDto>> PrintersOf(long bridgeId, CancellationToken ct)
    {
        var rows = await context.BridgePrinters.AsNoTracking()
            .Where(p => p.BridgeId == bridgeId)
            .OrderBy(p => p.Id)
            .Select(p => new { Entry = p, PrinterName = p.Printer.Name })
            .ToListAsync(ct);
        return rows.Select(r => ToDto(r.Entry, r.PrinterName)).ToList();
    }

    private static BridgePrinterDto ToDto(BridgePrinter p, string? printerName) => new()
    {
        Id = p.Id,
        PrinterId = p.PrinterId,
        PrinterName = printerName,
        Kind = p.Kind,
        Host = p.Host,
        Port = p.Port,
        Serial = p.Serial,
        IdentityKey = p.IdentityKey,
        Macs = Split(p.Macs),
        CreatedAt = Utc(p.CreatedDate),
    };

    private IQueryable<Bridge> Owned(long userId) => context.Bridges.Where(b => b.Grant.UserId == userId);

    public async Task<IReadOnlyList<BridgeDto>> ListAsync(long userId, CancellationToken ct)
        => await ListAsync(userId, bridgeId: null, ct);

    public async Task<BridgeDto?> GetAsync(long userId, long bridgeId, CancellationToken ct)
        => (await ListAsync(userId, bridgeId, ct)).SingleOrDefault();

    private async Task<IReadOnlyList<BridgeDto>> ListAsync(long userId, long? bridgeId, CancellationToken ct)
    {
        var rows = await Owned(userId).AsNoTracking()
            .Where(b => bridgeId == null || b.Id == bridgeId)
            .OrderBy(b => b.CreatedDate)
            .ThenBy(b => b.Id)
            .Select(b => new
            {
                Bridge = b,
                DeviceName = b.Grant.Name,
                PrinterCount = b.Printers!.Count(),
                ConnectionCount = context.Connections.Count(c => c.BridgeId == b.Id),
            })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Bridge.Id).ToList();
        var states = await StatesAsync(ids, ct);

        // Every bridge is printlog-bridge, and every bridge kind maps to its repository.
        var latest = await agentReleases.GetLatestVersionAsync(Connection.MoonrakerKind, ct);
        var now = clock.GetUtcNow();
        return rows.Select(r =>
        {
            var b = r.Bridge;
            var lastSeen = Utc(b.LastSeenAt);
            var found = states.Where(s => s.Row.BridgeId == b.Id).ToList();
            return new BridgeDto
            {
                Id = b.Id,
                Name = b.Name,
                DeviceName = r.DeviceName,
                DisplayName = b.Name ?? r.DeviceName,
                Location = b.Location,
                AgentVersion = b.AgentVersion,
                LatestAgentVersion = latest,
                UpdateAvailable = AgentVersion.IsOlder(b.AgentVersion, latest),
                Mode = b.Mode,
                HostName = b.HostName,
                Networks = Split(b.Networks),
                Status = now - lastSeen >= Connection.StaleAfter ? ConnectionStatus.Stale : ConnectionStatus.Online,
                LastSeenAt = lastSeen,
                CreatedAt = Utc(b.CreatedDate),
                ScanRequestedAt = Utc(b.ScanRequestedAt),
                LastSweepAt = Utc(b.LastSweepAt),
                PrinterCount = r.PrinterCount,
                ConnectionCount = r.ConnectionCount,
                DiscoveredCount = found.Count,
                DiscoveredNotConnectedCount = found.Count(s => s.State == DiscoveredPrinterState.New),
            };
        }).ToList();
    }

    private sealed record DiscoveredState(BridgeDiscoveredPrinter Row, DiscoveredPrinterState State, long? EntryId);

    /// <summary>
    /// Where each discovered printer of these bridges stands: connected (a desired printer has its
    /// identity, or its kind and address), configured, ignored, or new. Three queries.
    /// </summary>
    private async Task<List<DiscoveredState>> StatesAsync(IReadOnlyCollection<long> bridgeIds, CancellationToken ct)
    {
        var discovered = await context.BridgeDiscoveredPrinters.AsNoTracking()
            .Where(d => bridgeIds.Contains(d.BridgeId))
            .OrderBy(d => d.Id)
            .ToListAsync(ct);
        var desired = await context.BridgePrinters.AsNoTracking()
            .Where(p => bridgeIds.Contains(p.BridgeId))
            .Select(p => new { p.Id, p.BridgeId, p.Kind, p.Host, p.Port, p.IdentityKey })
            .ToListAsync(ct);
        var ignored = (await context.BridgeIgnoredPrinters.AsNoTracking()
                .Where(i => bridgeIds.Contains(i.BridgeId))
                .Select(i => new { i.BridgeId, i.IdentityKey })
                .ToListAsync(ct))
            .Select(i => (i.BridgeId, i.IdentityKey.ToLowerInvariant()))
            .ToHashSet();

        return discovered.Select(d =>
        {
            var entry = desired.FirstOrDefault(p => p.BridgeId == d.BridgeId
                && (string.Equals(p.IdentityKey, d.IdentityKey, StringComparison.OrdinalIgnoreCase)
                    || (p.Kind == d.Kind && string.Equals(p.Host, d.Address, StringComparison.OrdinalIgnoreCase)
                        && BridgeRules.SamePort(p.Kind, p.Port, d.Port))));
            // The bridge's config file wins over the app on the bridge, so Configured wins here
            // too; printerEntryId still names a matching app entry, so the UI can offer to
            // remove it.
            var state = d.Configured ? DiscoveredPrinterState.Configured
                : entry is not null ? DiscoveredPrinterState.Connected
                : ignored.Contains((d.BridgeId, d.IdentityKey.ToLowerInvariant())) ? DiscoveredPrinterState.Ignored
                : DiscoveredPrinterState.New;
            return new DiscoveredState(d, state, entry?.Id);
        }).ToList();
    }

    public async Task<bool> UpdateAsync(long userId, long bridgeId, string? name, string? location, CancellationToken ct)
    {
        var bridge = await Owned(userId).SingleOrDefaultAsync(b => b.Id == bridgeId, ct);
        if (bridge is null)
        {
            return false;
        }

        if (name is not null)
        {
            bridge.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }

        if (location is not null)
        {
            bridge.Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        }

        await context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<DiscoveredPrinterDto>?> ListDiscoveredAsync(long userId, long bridgeId, CancellationToken ct)
    {
        if (!await Owned(userId).AnyAsync(b => b.Id == bridgeId, ct))
        {
            return null;
        }

        return (await StatesAsync([bridgeId], ct)).Select(s => new DiscoveredPrinterDto
        {
            Id = s.Row.Id,
            Kind = s.Row.Kind,
            Address = s.Row.Address,
            Port = s.Row.Port,
            Model = s.Row.Model,
            Name = s.Row.Name,
            Serial = s.Row.Serial,
            MainboardId = s.Row.MainboardId,
            Macs = Split(s.Row.Macs),
            IdentityKey = s.Row.IdentityKey,
            State = s.State,
            PrinterEntryId = s.EntryId,
            FirstSeenAt = Utc(s.Row.FirstSeenAt),
            LastSeenAt = Utc(s.Row.LastSeenAt),
        }).ToList();
    }

    public async Task<IReadOnlyList<BridgePrinterDto>?> ListPrintersAsync(long userId, long bridgeId, CancellationToken ct)
        => await Owned(userId).AnyAsync(b => b.Id == bridgeId, ct) ? await PrintersOf(bridgeId, ct) : null;

    public async Task<BridgePrinterDto?> ConnectAsync(long userId, long bridgeId, ConnectBridgePrinterDto body, CancellationToken ct)
    {
        var bridge = await Owned(userId).SingleOrDefaultAsync(b => b.Id == bridgeId, ct);
        if (bridge is null)
        {
            return null;
        }

        if (body.PrinterId is not { } printerId)
        {
            throw Bad("printer_not_found", "printerId is required: one of your printers.");
        }

        var printer = await context.Printers
            .Where(p => p.Id == printerId && p.UserId == userId)
            .Select(p => new { p.Name })
            .SingleOrDefaultAsync(ct)
            ?? throw Bad("printer_not_found", "The printer does not exist or is not yours.");
        var printerName = printer.Name;

        var entry = await NewEntryAsync(bridgeId, printerId, body, ct);

        if (await context.BridgePrinters.CountAsync(p => p.BridgeId == bridgeId, ct) >= Bridge.MaxPrinters)
        {
            throw new BridgeProblemException(StatusCodes.Status409Conflict, "bridge_printer_limit",
                $"A bridge watches at most {Bridge.MaxPrinters} printers from the app.");
        }

        await EnsureNotConnectedAsync(userId, entry, ct);

        entry.CreatedDate = clock.GetUtcNow().UtcDateTime;
        context.BridgePrinters.Add(entry);
        bridge.DesiredRevision = Guid.NewGuid();
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent connect of the same printer won the unique index, or the printer was
            // deleted meanwhile. Either way, say which.
            context.ChangeTracker.Clear();
            await EnsureNotConnectedAsync(userId, entry, ct);
            if (!await context.Printers.AnyAsync(p => p.Id == printerId && p.UserId == userId, ct))
            {
                throw Bad("printer_not_found", "The printer does not exist or is not yours.");
            }

            throw;
        }

        return ToDto(entry, printerName);
    }

    /// <summary>Builds the entry from the discovered printer, or from the body alone when connecting by hand.</summary>
    private async Task<BridgePrinter> NewEntryAsync(long bridgeId, long printerId, ConnectBridgePrinterDto body, CancellationToken ct)
    {
        BridgeDiscoveredPrinter? found = null;
        if (body.DiscoveredPrinterId is { } discoveredId)
        {
            found = await context.BridgeDiscoveredPrinters.AsNoTracking()
                .SingleOrDefaultAsync(d => d.Id == discoveredId && d.BridgeId == bridgeId, ct)
                ?? throw Bad("discovered_printer_not_found", "This bridge has not found a printer with that id.");
            if (found.Configured)
            {
                throw ConfiguredOn(bridgeId);
            }
        }

        string kind;
        if (body.Kind is null && found is not null)
        {
            kind = found.Kind;
        }
        else if (BridgeRules.Kind(body.Kind) is { } bodyKind && (found is null || bodyKind == found.Kind))
        {
            kind = bodyKind;
        }
        else
        {
            throw Bad("invalid_kind", $"kind must be one of {string.Join(", ", Connection.BridgeKinds)}, and match the discovered printer's.");
        }

        var host = body.Host is null && found is not null ? found.Address : BridgeRules.Host(body.Host)
            ?? throw Bad("invalid_host", "host must be a private IP address, or a host name that is a single label or ends in .local, .lan, .home.arpa, .internal or .localdomain.");

        var port = body.Port ?? found?.Port;
        if (!BridgeRules.IsPort(port))
        {
            throw Bad("invalid_port", "port must be 1 to 65535.");
        }

        var serial = string.IsNullOrWhiteSpace(body.Serial) ? found?.Serial : body.Serial.Trim();
        if (serial is not null && !BridgeRules.IsId(serial))
        {
            throw Bad("invalid_serial", "serial: up to 64 of letters, digits and . _ : -.");
        }

        if (kind == "bambu" && serial is null)
        {
            throw Bad("serial_required", "A Bambu Lab printer needs its serial number.");
        }

        return new BridgePrinter
        {
            BridgeId = bridgeId,
            PrinterId = printerId,
            Kind = kind,
            Host = host,
            Port = port,
            Serial = serial,
            // A hand entry gets an identity in the same form as a discovery, so the same address
            // (or serial) cannot be connected twice.
            IdentityKey = found?.IdentityKey ?? BridgeRules.IdentityKey(kind, serial, null, [], host, port),
            Macs = found?.Macs,
        };
    }

    /// <summary>
    /// Refuses every way the same printer could end up watched twice, since each watcher has its
    /// own instance id and nothing downstream dedupes their prints: the 3D Print Log printer on
    /// any bridge, the same identity or address on this bridge, and the same stable identity
    /// (serial, mainboard id, MAC) on another of the user's bridges, and a printer a bridge
    /// reported as in its config file. Moving a printer between bridges is printlog-bridge#103's;
    /// until then the 409 names the bridge that has it.
    /// <para>
    /// Known limit (left for printlog-bridge#103): across bridges only the derived identity key
    /// is compared, so two bridges that identify one printer by different fields (a MAC on one
    /// segment, an address across a VLAN, a serial only one of them sees) are not caught; and
    /// two concurrent connects of one serial to two bridges under different printers can both
    /// pass, since no unique constraint spans bridges.
    /// </para>
    /// </summary>
    private async Task EnsureNotConnectedAsync(long userId, BridgePrinter entry, CancellationToken ct)
    {
        var printerOn = await context.BridgePrinters
            .Where(p => p.PrinterId == entry.PrinterId)
            .Select(p => (long?)p.BridgeId)
            .FirstOrDefaultAsync(ct);
        if (printerOn == entry.BridgeId)
        {
            throw new BridgeProblemException(StatusCodes.Status409Conflict, "printer_already_connected",
                "That printer is already connected to this bridge.");
        }

        if (printerOn is { } elsewhere)
        {
            throw ConnectedElsewhere(elsewhere);
        }

        var onThisBridge = await context.BridgePrinters.AsNoTracking()
            .Where(p => p.BridgeId == entry.BridgeId)
            .Select(p => new { p.Kind, p.Host, p.Port, p.IdentityKey })
            .ToListAsync(ct);
        if (onThisBridge.Any(p =>
                string.Equals(p.IdentityKey, entry.IdentityKey, StringComparison.OrdinalIgnoreCase)
                || (p.Kind == entry.Kind && string.Equals(p.Host, entry.Host, StringComparison.OrdinalIgnoreCase)
                    && BridgeRules.SamePort(p.Kind, p.Port, entry.Port))))
        {
            throw new BridgeProblemException(StatusCodes.Status409Conflict, "discovered_printer_already_connected",
                "That printer is already connected to this bridge.");
        }

        // An address means nothing across networks; a serial, mainboard id or MAC does.
        if (entry.IdentityKey is { } key && !key.Contains(":ip:", StringComparison.Ordinal))
        {
            var otherBridge = await context.BridgePrinters
                .Where(p => p.IdentityKey == key && p.BridgeId != entry.BridgeId && p.Bridge.Grant.UserId == userId)
                .Select(p => (long?)p.BridgeId)
                .FirstOrDefaultAsync(ct);
            if (otherBridge is { } other)
            {
                throw ConnectedElsewhere(other);
            }
        }

        // A bridge watching the printer from its config file is a watcher too. On this bridge it
        // is matched like an app entry; across bridges, by a stable identity only.
        var configuredHere = await context.BridgeDiscoveredPrinters.AsNoTracking()
            .Where(d => d.BridgeId == entry.BridgeId && d.Configured)
            .Select(d => new { d.Kind, d.Address, d.Port, d.IdentityKey })
            .ToListAsync(ct);
        if (configuredHere.Any(d =>
                string.Equals(d.IdentityKey, entry.IdentityKey, StringComparison.OrdinalIgnoreCase)
                || (d.Kind == entry.Kind && string.Equals(d.Address, entry.Host, StringComparison.OrdinalIgnoreCase)
                    && BridgeRules.SamePort(d.Kind, d.Port, entry.Port))))
        {
            throw ConfiguredOn(entry.BridgeId);
        }

        if (entry.IdentityKey is { } stable && !stable.Contains(":ip:", StringComparison.Ordinal))
        {
            var configuredElsewhere = await context.BridgeDiscoveredPrinters
                .Where(d => d.Configured && d.IdentityKey == stable && d.BridgeId != entry.BridgeId && d.Bridge.Grant.UserId == userId)
                .Select(d => (long?)d.BridgeId)
                .FirstOrDefaultAsync(ct);
            if (configuredElsewhere is { } other)
            {
                throw ConfiguredOn(other);
            }
        }
    }

    private static BridgeProblemException ConfiguredOn(long bridgeId) => new(StatusCodes.Status409Conflict,
        "printer_configured_on_bridge", "A bridge already watches this printer from its config file.", bridgeId);

    private static BridgeProblemException ConnectedElsewhere(long bridgeId) => new(StatusCodes.Status409Conflict,
        "printer_connected_elsewhere", "That printer is already watched by another of your bridges.", bridgeId);

    private static BridgeProblemException Bad(string code, string detail) => new(StatusCodes.Status400BadRequest, code, detail);

    public async Task<bool> DisconnectAsync(long userId, long bridgeId, long entryId, CancellationToken ct)
    {
        var bridge = await Owned(userId).SingleOrDefaultAsync(b => b.Id == bridgeId, ct);
        var entry = bridge is null ? null : await context.BridgePrinters.SingleOrDefaultAsync(p => p.Id == entryId && p.BridgeId == bridgeId, ct);
        if (bridge is null || entry is null)
        {
            return false;
        }

        context.BridgePrinters.Remove(entry);
        bridge.DesiredRevision = Guid.NewGuid();
        await context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetIgnoredAsync(long userId, long bridgeId, long discoveredId, bool ignored, CancellationToken ct)
    {
        var key = await context.BridgeDiscoveredPrinters
            .Where(d => d.Id == discoveredId && d.BridgeId == bridgeId && d.Bridge.Grant.UserId == userId)
            .Select(d => d.IdentityKey)
            .SingleOrDefaultAsync(ct);
        if (key is null)
        {
            return false;
        }

        var existing = await context.BridgeIgnoredPrinters.Where(i => i.BridgeId == bridgeId && i.IdentityKey == key).ToListAsync(ct);
        if (!ignored)
        {
            context.BridgeIgnoredPrinters.RemoveRange(existing);
            await context.SaveChangesAsync(ct);
            return true;
        }

        if (existing.Count > 0)
        {
            return true;
        }

        if (await context.BridgeIgnoredPrinters.CountAsync(i => i.BridgeId == bridgeId, ct) >= Bridge.MaxIgnoredPrinters)
        {
            throw new BridgeProblemException(StatusCodes.Status409Conflict, "ignore_limit",
                $"A bridge keeps at most {Bridge.MaxIgnoredPrinters} ignored printers.");
        }

        context.BridgeIgnoredPrinters.Add(new BridgeIgnoredPrinter { BridgeId = bridgeId, IdentityKey = key, CreatedDate = clock.GetUtcNow().UtcDateTime });
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (UniqueConstraintViolation.IsUniqueViolation(ex))
        {
            // A second tap raced the first; it is ignored either way.
            context.ChangeTracker.Clear();
        }

        return true;
    }

    public async Task<DateTimeOffset?> RequestScanAsync(long userId, long bridgeId, CancellationToken ct)
    {
        var bridge = await Owned(userId).SingleOrDefaultAsync(b => b.Id == bridgeId, ct);
        if (bridge is null)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (bridge.ScanRequestedAt is not { } requested || now - requested >= ScanRequestWindow)
        {
            bridge.ScanRequestedAt = now;
            bridge.DesiredRevision = Guid.NewGuid();
            await context.SaveChangesAsync(ct);
        }

        return Utc(bridge.ScanRequestedAt);
    }

    private static List<string> Split(string? stored) => stored?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

    // Stored as UTC, but SQLite hands it back as Unspecified.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}
