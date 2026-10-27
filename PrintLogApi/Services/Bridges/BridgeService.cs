using System.Globalization;
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

    /// <summary>The bridge's current key, for <c>bridge_key_changed</c>.</summary>
    public string? KeyFingerprint { get; init; }
}

/// <summary>The device grant behind a check-in was revoked while the check-in ran.</summary>
public sealed class BridgeGrantRevokedException : Exception;

/// <summary>
/// A check-in's result: the desired printers, whether the bridge was registered just now, entries
/// dropped against the stored state (credential issues), the key the API holds, and whether the
/// check-in changed it.
/// </summary>
public sealed record BridgeCheckInResult(DesiredPrintersDto Desired, bool Registered)
{
    public IReadOnlyList<RejectedEntryDto> Rejected { get; init; } = [];

    public string? PublicKeyFingerprint { get; init; }

    public string? PendingPublicKeyFingerprint { get; init; }

    /// <summary>What this check-in did to the key: <c>first</c> (taken), <c>pending</c> (held for confirmation), <c>throttled</c> (a new key past the daily cap, not stored), or null.</summary>
    public string? KeyChange { get; init; }

    /// <summary>The bridge's key changes today, this one included, when <see cref="KeyChange"/> is set.</summary>
    public int KeyChangesToday { get; init; }
}

/// <summary>The result of confirming a pending key, for the controller's telemetry.</summary>
public sealed record BridgeKeyConfirmation(BridgeDto Bridge, int KeyChangesToday);

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

    /// <summary>
    /// Stores one sealed credential of a desired printer and returns the entry as the owner sees
    /// it (without the sealed value). Null when the bridge or entry isn't the user's;
    /// <see cref="BridgeProblemException"/> for a refusal. Never decrypts.
    /// </summary>
    Task<BridgePrinterDto?> SetSecretAsync(long userId, long bridgeId, long entryId, string field, SetBridgePrinterSecretDto body, CancellationToken ct);

    /// <summary>Clears one sealed credential. False when the bridge or entry isn't the user's.</summary>
    Task<bool> ClearSecretAsync(long userId, long bridgeId, long entryId, string field, CancellationToken ct);

    /// <summary>
    /// Makes the bridge's pending key current, if <paramref name="fingerprint"/> is its
    /// fingerprint: earlier values go stale and the desired version changes. Null when the user
    /// has no such bridge; <see cref="BridgeProblemException"/> for a refusal.
    /// </summary>
    Task<BridgeKeyConfirmation?> ConfirmPendingKeyAsync(long userId, long bridgeId, string? fingerprint, CancellationToken ct);
}

/// <summary>
/// Bridges, what they find and what they are told to watch (printlog-bridge#100, #103). Nothing
/// here touches the user's cache version: no cached response reads bridges, and a check-in every
/// few minutes must not flush the summaries.
/// </summary>
public sealed class BridgeService(
    PrintLogContext context,
    IAgentReleaseCatalog agentReleases,
    TimeProvider clock,
    INotificationService notifications,
    ILogger<BridgeService> logger) : IBridgeService
{
    /// <summary>
    /// Key changes a bridge may make in a UTC day before a new key stops replacing the pending
    /// one, so a flapping (or hostile) bridge can't keep moving the key the owner is comparing.
    /// </summary>
    public const int MaxKeyChangesPerDay = 5;

    /// <summary>A second "Scan now" within this window is the same request.</summary>
    public static readonly TimeSpan ScanRequestWindow = TimeSpan.FromMinutes(1);

    /// <summary>A poll refreshes the bridge's last-seen time at most this often.</summary>
    public static readonly TimeSpan PollSeenResolution = TimeSpan.FromMinutes(2);

    /// <summary>The credential issues one check-in may report: every field of every printer.</summary>
    public const int MaxCredentialIssues = 200;

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

        // Issues first: they describe the desired version as it was before this check-in.
        var rejected = new List<RejectedEntryDto>();
        if (report.CredentialIssues is { } issues && !registered && report.CredentialIssuesVersion is { } issuesVersion)
        {
            await ReplaceCredentialIssuesAsync(bridge.Id, issuesVersion, issues, rejected, ct);
        }

        var hadPending = bridge.PendingPublicKey is not null;
        var keyChange = ApplyReportedKey(bridge, report, now, registered);
        var notify = keyChange == "pending" && !hadPending && bridge.KeyNotifiedDay != now.Date;
        if (notify)
        {
            bridge.KeyNotifiedDay = now.Date;
        }

        await context.SaveChangesAsync(ct);

        if (keyChange == "pending")
        {
            // A check-in that read the bridge before a confirmation can write a pending key the
            // owner just made current. Left there it would refuse every seal as unconfirmed.
            var cleared = await context.Bridges
                .Where(b => b.Id == bridge.Id && b.PendingPublicKey != null && b.PendingPublicKey == b.PublicKey)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(b => b.PendingPublicKey, (string?)null)
                    .SetProperty(b => b.PendingPublicKeyFingerprint, (string?)null)
                    .SetProperty(b => b.PendingPublicKeyAt, (DateTime?)null), ct);
            if (cleared > 0)
            {
                await context.Entry(bridge).ReloadAsync(ct);
                notify = false;
            }
        }

        if (notify)
        {
            await NotifyKeyPendingAsync(grantId, bridge.Name, ct);
        }

        return new BridgeCheckInResult(await DesiredAsync(bridge, ct), registered)
        {
            Rejected = rejected,
            PublicKeyFingerprint = bridge.PublicKeyFingerprint,
            PendingPublicKeyFingerprint = bridge.PendingPublicKeyFingerprint,
            KeyChange = keyChange,
            KeyChangesToday = bridge.KeyChangesToday,
        };
    }

    /// <summary>
    /// A key reported on the check-in that registers the bridge is taken as is (trust on first
    /// use, at pairing). Any other new key, including the first one from a bridge that
    /// registered without a key, is held pending until the owner confirms it by its
    /// fingerprint, because a stolen device token could report its own key and collect what the
    /// user seals next. Reporting the current key again leaves a pending one alone (and clears
    /// one equal to it); another new key replaces it, up to MaxKeyChangesPerDay. Returns
    /// first, pending, throttled (once a day, when the cap kept the pending key), or null.
    /// </summary>
    private static string? ApplyReportedKey(Bridge bridge, ValidCheckIn report, DateTime now, bool registered)
    {
        if (report.PublicKey is not { } key)
        {
            return null;
        }

        if (key == bridge.PublicKey)
        {
            // A pending key equal to the current one is left over from a race; it would block seals.
            if (bridge.PendingPublicKey == key)
            {
                ClearPendingKey(bridge);
            }

            return null;
        }

        if (key == bridge.PendingPublicKey)
        {
            return null;
        }

        if (bridge.PendingPublicKey is not null && bridge.KeyChangesDay == now.Date && bridge.KeyChangesToday >= MaxKeyChangesPerDay)
        {
            // Past the cap, keep the pending key the owner may be comparing. Counted (and
            // reported) once, which is what flags the bridge as throttled for the rest of the day.
            if (bridge.KeyChangesToday > MaxKeyChangesPerDay)
            {
                return null;
            }

            CountKeyChange(bridge, now);
            return "throttled";
        }

        string change;
        if (bridge.PublicKey is null && registered)
        {
            bridge.PublicKey = key;
            bridge.PublicKeyFingerprint = report.PublicKeyFingerprint;
            bridge.PublicKeyChangedAt = now;
            bridge.DesiredRevision = Guid.NewGuid();
            change = "first";
        }
        else
        {
            bridge.PendingPublicKey = key;
            bridge.PendingPublicKeyFingerprint = report.PublicKeyFingerprint;
            bridge.PendingPublicKeyAt = now;
            change = "pending";
        }

        CountKeyChange(bridge, now);
        return change;
    }

    /// <summary>Clears a bridge's pending key (re-pairing does too, in <c>DeviceGrantService</c>).</summary>
    public static void ClearPendingKey(Bridge bridge)
    {
        bridge.PendingPublicKey = null;
        bridge.PendingPublicKeyFingerprint = null;
        bridge.PendingPublicKeyAt = null;
    }

    /// <summary>
    /// Tells the owner a bridge reported a new key. After the check-in's save, never inside a
    /// transaction (a push can't be recalled), and best effort: the check-in already committed,
    /// and the pending key also shows on the Bridges screen.
    /// </summary>
    private async Task NotifyKeyPendingAsync(long grantId, string? bridgeName, CancellationToken ct)
    {
        try
        {
            var grant = await context.DeviceGrants.AsNoTracking().Where(g => g.Id == grantId).Select(g => new { g.UserId, g.Name }).SingleAsync(ct);
            await notifications.CreateBridgeKeyPendingNotification(grant.UserId, bridgeName ?? grant.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not notify the owner of bridge {GrantId} about a pending key", grantId);
        }
    }

    private static void CountKeyChange(Bridge bridge, DateTime now)
    {
        if (bridge.KeyChangesDay != now.Date)
        {
            bridge.KeyChangesDay = now.Date;
            bridge.KeyChangesToday = 0;
        }

        bridge.KeyChangesToday++;
    }

    /// <summary>
    /// Replaces what the bridge reported it can't use, on all its entries, if the report is about
    /// the current desired version. An entry that isn't its own, a field the entry's kind doesn't
    /// take, or an unknown code is dropped and named.
    /// <para>
    /// Race with an owner's seal (which clears that field's issue and moves the version): the
    /// version is read after the entries, so a seal committed before that read stops the report;
    /// one committed after it changes the entry's <see cref="BridgePrinter.IssuesStamp"/>, so
    /// this write fails its concurrency check and the check-in reruns against the new version.
    /// </para>
    /// </summary>
    private async Task ReplaceCredentialIssuesAsync(long bridgeId, string reportedVersion, IReadOnlyList<ReportedCredentialIssue> issues, List<RejectedEntryDto> rejected, CancellationToken ct)
    {
        var entries = await context.BridgePrinters.Where(p => p.BridgeId == bridgeId).ToListAsync(ct);
        var current = await context.Bridges.AsNoTracking().Where(b => b.Id == bridgeId).Select(b => b.DesiredRevision).SingleAsync(ct);
        if (reportedVersion != Version(current))
        {
            return;
        }

        var byEntry = entries.ToDictionary(e => e.Id, _ => new SortedDictionary<string, string>(StringComparer.Ordinal));
        foreach (var issue in issues)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"credentialIssues[{issue.Index}]");
            var entry = issue.EntryId is { } id ? entries.FirstOrDefault(e => e.Id == id) : null;
            if (entry is null)
            {
                rejected.Add(new RejectedEntryDto { Field = name, Code = "unknown_printer_entry" });
            }
            else if (!SealedCredentials.IsField(entry.Kind, issue.Field))
            {
                rejected.Add(new RejectedEntryDto { Field = name, Code = "invalid_secret_field" });
            }
            else if (issue.Code is null || !SealedCredentials.IssueCodes.Contains(issue.Code))
            {
                rejected.Add(new RejectedEntryDto { Field = name, Code = "invalid_issue_code" });
            }
            else
            {
                byEntry[entry.Id][issue.Field!] = issue.Code;
            }
        }

        foreach (var entry in entries)
        {
            var found = byEntry[entry.Id];
            var stored = found.Count == 0 ? null : string.Join(' ', found.Select(i => $"{i.Key}:{i.Value}"));
            if (stored != entry.CredentialIssues)
            {
                entry.CredentialIssues = stored;
                entry.IssuesStamp = Guid.NewGuid();
            }
        }
    }

    private static List<CredentialIssueDto> Issues(string? stored) => Split(stored)
        .Select(pair => pair.Split(':', 2))
        .Where(pair => pair.Length == 2)
        .Select(pair => new CredentialIssueDto { Field = pair[0], Code = pair[1] })
        .ToList();

    private static string? WithoutIssue(string? stored, string field)
    {
        var kept = Split(stored).Where(pair => !pair.StartsWith(field + ":", StringComparison.Ordinal)).ToList();
        return kept.Count == 0 ? null : string.Join(' ', kept);
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

    /// <summary>The bridge's own view: the only place sealed values ever leave the API.</summary>
    private async Task<DesiredPrintersDto> DesiredAsync(Bridge bridge, CancellationToken ct) => new()
    {
        Version = Version(bridge.DesiredRevision),
        ScanRequestedAt = Utc(bridge.ScanRequestedAt),
        Printers = await PrintersOf(bridge.Id, bridge.PublicKeyFingerprint, forBridge: true, ct),
    };

    private static string Version(Guid revision) => revision.ToString("N");

    /// <summary>
    /// The desired printers with their sealed credentials. For the bridge
    /// (<paramref name="forBridge"/>): only values sealed to its current key, with the sealed
    /// value. For the owner: every value, stale ones marked, never the sealed value.
    /// </summary>
    private async Task<List<BridgePrinterDto>> PrintersOf(long bridgeId, string? currentFingerprint, bool forBridge, CancellationToken ct)
    {
        var rows = await context.BridgePrinters.AsNoTracking()
            .Where(p => p.BridgeId == bridgeId)
            .OrderBy(p => p.Id)
            .Select(p => new { Entry = p, PrinterName = p.Printer.Name })
            .ToListAsync(ct);
        var secrets = (await context.BridgePrinterSecrets.AsNoTracking()
                .Where(s => s.BridgePrinter.BridgeId == bridgeId)
                .OrderBy(s => s.Id)
                .ToListAsync(ct))
            .ToLookup(s => s.BridgePrinterId);
        return rows.Select(r => ToDto(r.Entry, r.PrinterName, secrets[r.Entry.Id], currentFingerprint, forBridge)).ToList();
    }

    private static BridgePrinterDto ToDto(BridgePrinter p, string? printerName, IEnumerable<BridgePrinterSecret> secrets, string? currentFingerprint, bool forBridge) => new()
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
        SecretFields = [.. SealedCredentials.FieldsFor(p.Kind)],
        Secrets = secrets
            .Select(s => (Secret: s, Stale: currentFingerprint is null || s.KeyFingerprint != currentFingerprint))
            .Where(s => !forBridge || !s.Stale)
            .Select(s => new BridgePrinterSecretDto
            {
                Field = s.Secret.Field,
                Sealed = forBridge ? s.Secret.SealedValue : null,
                KeyFingerprint = s.Secret.KeyFingerprint,
                UpdatedAt = Utc(s.Secret.UpdatedDate),
                Stale = s.Stale,
            })
            .ToList(),
        CredentialIssues = Issues(p.CredentialIssues),
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
                PublicKey = b.PublicKey,
                PublicKeyFingerprint = b.PublicKeyFingerprint,
                PublicKeyChangedAt = Utc(b.PublicKeyChangedAt),
                PendingPublicKey = b.PendingPublicKey,
                PendingPublicKeyFingerprint = b.PendingPublicKeyFingerprint,
                PendingPublicKeyAt = Utc(b.PendingPublicKeyAt),
                KeyChangesThrottled = b.KeyChangesDay == now.UtcDateTime.Date && b.KeyChangesToday > MaxKeyChangesPerDay,
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
    {
        var bridge = await Owned(userId).Where(b => b.Id == bridgeId).Select(b => new { b.PublicKeyFingerprint }).SingleOrDefaultAsync(ct);
        return bridge is null ? null : await PrintersOf(bridgeId, bridge.PublicKeyFingerprint, forBridge: false, ct);
    }

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

        return ToDto(entry, printerName, [], bridge.PublicKeyFingerprint, forBridge: false);
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
        for (var attempt = 0; ; attempt++)
        {
            var bridge = await Owned(userId).SingleOrDefaultAsync(b => b.Id == bridgeId, ct);
            var entry = bridge is null ? null : await context.BridgePrinters.SingleOrDefaultAsync(p => p.Id == entryId && p.BridgeId == bridgeId, ct);
            if (bridge is null || entry is null)
            {
                return false;
            }

            context.BridgePrinters.Remove(entry);
            bridge.DesiredRevision = Guid.NewGuid();
            try
            {
                await context.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                // A check-in wrote the entry's issues (its concurrency stamp) meanwhile.
                context.ChangeTracker.Clear();
            }
        }
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

    public async Task<BridgePrinterDto?> SetSecretAsync(long userId, long bridgeId, long entryId, string field, SetBridgePrinterSecretDto body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var bridge = await Owned(userId).SingleOrDefaultAsync(b => b.Id == bridgeId, ct);
            var entry = bridge is null ? null : await context.BridgePrinters.SingleOrDefaultAsync(p => p.Id == entryId && p.BridgeId == bridgeId, ct);
            if (bridge is null || entry is null)
            {
                return null;
            }

            EnsureField(entry, field);
            if (!SealedCredentials.IsSealedValue(body.Sealed))
            {
                throw Bad("invalid_sealed_value",
                    "sealed: v1. and unpadded base64url of an uncompressed P-256 point, a 12-byte nonce, 1 to 256 bytes of ciphertext and a 16-byte tag (docs/bridge-credential-sealing.md).");
            }

            if (!SealedCredentials.IsFingerprint(body.KeyFingerprint))
            {
                throw Bad("invalid_key_fingerprint", "keyFingerprint: the bridge's publicKeyFingerprint you sealed to, 64 lower-case hex characters.");
            }

            // The current key first: a pending key equal to it (left by a race) must not refuse
            // a seal to the key the bridge actually uses.
            if (body.KeyFingerprint != bridge.PublicKeyFingerprint)
            {
                if (body.KeyFingerprint == bridge.PendingPublicKeyFingerprint)
                {
                    throw new BridgeProblemException(StatusCodes.Status409Conflict, "bridge_key_unconfirmed",
                        "The bridge reported a new key that hasn't been confirmed. Compare its fingerprint with the bridge's and confirm it first.")
                    {
                        KeyFingerprint = bridge.PublicKeyFingerprint,
                    };
                }

                if (bridge.PublicKeyFingerprint is null)
                {
                    throw new BridgeProblemException(StatusCodes.Status409Conflict, "bridge_key_missing",
                        "This bridge hasn't reported a key to seal credentials to. Update printlog-bridge.");
                }

                throw new BridgeProblemException(StatusCodes.Status409Conflict, "bridge_key_changed",
                    "The bridge's key changed. Check its fingerprint and seal the credential again.")
                {
                    KeyFingerprint = bridge.PublicKeyFingerprint,
                };
            }

            var secret = await context.BridgePrinterSecrets.SingleOrDefaultAsync(s => s.BridgePrinterId == entryId && s.Field == field, ct);
            if (secret is null)
            {
                secret = new BridgePrinterSecret { BridgePrinterId = entryId, Field = field };
                context.BridgePrinterSecrets.Add(secret);
            }

            secret.SealedValue = body.Sealed!;
            secret.KeyFingerprint = body.KeyFingerprint!;
            secret.UpdatedDate = clock.GetUtcNow().UtcDateTime;
            entry.CredentialIssues = WithoutIssue(entry.CredentialIssues, field);
            // Always, even when there was no issue to clear: a check-in that read this entry
            // before now must fail its write instead of restoring an issue about the old value.
            entry.IssuesStamp = Guid.NewGuid();
            bridge.DesiredRevision = Guid.NewGuid();
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (attempt < 2 && (ex is DbUpdateConcurrencyException || UniqueConstraintViolation.IsUniqueViolation(ex)))
            {
                // A concurrent first seal of the same field inserted it (this one replaces it),
                // or a check-in wrote the entry's issues meanwhile: read again and retry.
                context.ChangeTracker.Clear();
                continue;
            }

            return (await ListPrintersAsync(userId, bridgeId, ct))?.SingleOrDefault(p => p.Id == entryId);
        }
    }

    public async Task<bool> ClearSecretAsync(long userId, long bridgeId, long entryId, string field, CancellationToken ct)
    {
        var bridge = await Owned(userId).SingleOrDefaultAsync(b => b.Id == bridgeId, ct);
        var entry = bridge is null ? null : await context.BridgePrinters.SingleOrDefaultAsync(p => p.Id == entryId && p.BridgeId == bridgeId, ct);
        if (bridge is null || entry is null)
        {
            return false;
        }

        EnsureField(entry, field);
        var secret = await context.BridgePrinterSecrets.SingleOrDefaultAsync(s => s.BridgePrinterId == entryId && s.Field == field, ct);
        if (secret is not null)
        {
            // One SaveChanges: the bridge must never miss a clear because the version didn't move.
            context.BridgePrinterSecrets.Remove(secret);
            bridge.DesiredRevision = Guid.NewGuid();
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent clear removed it first, and bumped the version.
                context.ChangeTracker.Clear();
            }
        }

        return true;
    }

    public async Task<BridgeKeyConfirmation?> ConfirmPendingKeyAsync(long userId, long bridgeId, string? fingerprint, CancellationToken ct)
    {
        var bridge = await Owned(userId).AsNoTracking().Where(b => b.Id == bridgeId)
            .Select(b => new { b.PendingPublicKeyFingerprint, b.KeyChangesDay, b.KeyChangesToday })
            .SingleOrDefaultAsync(ct);
        if (bridge is null)
        {
            return null;
        }

        if (!SealedCredentials.IsFingerprint(fingerprint))
        {
            throw Bad("invalid_key_fingerprint", "fingerprint: the pending key's fingerprint you computed from pendingPublicKey, 64 lower-case hex characters.");
        }

        if (bridge.PendingPublicKeyFingerprint is null)
        {
            throw new BridgeProblemException(StatusCodes.Status409Conflict, "no_pending_key", "This bridge has no new key waiting for confirmation.");
        }

        // One conditional statement: a check-in that replaced the pending key meanwhile makes
        // this match nothing, so the key confirmed is always the one the owner checked.
        var now = clock.GetUtcNow().UtcDateTime;
        var changesToday = bridge.KeyChangesDay == now.Date ? bridge.KeyChangesToday + 1 : 1;
        // Ownership was checked above, and a bridge never changes owner.
        var revision = Guid.NewGuid();
        var updated = await context.Bridges
            .Where(b => b.Id == bridgeId && b.PendingPublicKeyFingerprint == fingerprint && b.PendingPublicKey != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.PublicKey, b => b.PendingPublicKey)
                .SetProperty(b => b.PublicKeyFingerprint, b => b.PendingPublicKeyFingerprint)
                .SetProperty(b => b.PublicKeyChangedAt, now)
                .SetProperty(b => b.PendingPublicKey, (string?)null)
                .SetProperty(b => b.PendingPublicKeyFingerprint, (string?)null)
                .SetProperty(b => b.PendingPublicKeyAt, (DateTime?)null)
                .SetProperty(b => b.KeyChangesDay, now.Date)
                .SetProperty(b => b.KeyChangesToday, changesToday)
                .SetProperty(b => b.DesiredRevision, revision), ct);
        if (updated == 0)
        {
            throw new BridgeProblemException(StatusCodes.Status409Conflict, "pending_key_changed",
                "That isn't the bridge's pending key; it may have reported another. Reload the bridge and compare again.");
        }

        return new BridgeKeyConfirmation((await GetAsync(userId, bridgeId, ct))!, changesToday);
    }

    private static void EnsureField(BridgePrinter entry, string field)
    {
        if (!SealedCredentials.IsField(entry.Kind, field))
        {
            var fields = SealedCredentials.FieldsFor(entry.Kind);
            throw Bad("invalid_secret_field", fields.Count == 0
                ? $"A {entry.Kind} printer takes no credentials."
                : $"A {entry.Kind} printer takes {string.Join(", ", fields)}.");
        }
    }

    private static List<string> Split(string? stored) => stored?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

    // Stored as UTC, but SQLite hands it back as Unspecified.
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}
