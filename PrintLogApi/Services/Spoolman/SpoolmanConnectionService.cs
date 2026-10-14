using System.Net;
using System.Security;
using System.Security.Cryptography;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Spoolman;

namespace PrintLogApi.Services.Spoolman;

public interface ISpoolmanConnectionService
{
    /// <summary>
    /// Makes the printer's Spoolman connection, revoking the one it had, and returns the secret
    /// once. Null when the printer is not the user's. Throws
    /// <see cref="SpoolmanConnectionLimitException"/> when Free's one connection is taken by
    /// another printer.
    /// </summary>
    /// <param name="requestOrigin">The request's scheme and host, used when <c>Spoolman:ServerUrl</c> is blank.</param>
    Task<SpoolmanConnectionCreatedDto?> CreateAsync(long userId, long printerId, string requestOrigin, CancellationToken ct);

    /// <summary>All of the user's connections, revoked ones included, newest first.</summary>
    Task<IReadOnlyList<SpoolmanConnectionDto>> ListAsync(long userId, CancellationToken ct);

    /// <summary>Revokes one of the user's connections. False when it is not theirs.</summary>
    Task<bool> RevokeAsync(long userId, Guid connectionId, CancellationToken ct);

    /// <summary>
    /// Brings the user's connections in line with their tier: on Free, every active connection
    /// but the oldest is paused; on Pro, every connection a downgrade paused is resumed. Returns
    /// the ids it changed. Idempotent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ApplyTierAsync(long userId, CancellationToken ct);

    /// <summary>
    /// Records that one of the user's connections connected from <paramref name="address"/>'s
    /// network, and emails the admin when that makes more than the fair-use number this week.
    /// </summary>
    Task RecordNetworkAsync(long userId, IPAddress address, CancellationToken ct);
}

/// <summary>
/// Spoolman connection tokens, their tier limits and the fair-use alert (#175).
///
/// <para><b>Tier rule.</b> Free allows one <i>active</i> connection. Paused (disabled) and
/// revoked ones do not count, so a user downgraded from Pro can still connect a different
/// printer once they disconnect the one that stayed active. A downgrade pauses rather than
/// deletes, keeping the oldest active, and re-subscribing resumes everything it paused.</para>
///
/// <para><b>"Is Pro"</b> is the direct query on <c>Subscriptions</c> used everywhere else (see
/// <c>PrinterService.GetMaxImagesPerPrinter</c>), not a helper, so there is one definition.</para>
/// </summary>
public sealed class SpoolmanConnectionService(
    PrintLogContext context,
    IOptions<SpoolmanOptions> options,
    IOptions<EmailOptions> emailOptions,
    IEmailSender emailSender,
    TimeProvider clock,
    TelemetryClient telemetry,
    ILogger<SpoolmanConnectionService> logger) : ISpoolmanConnectionService
{
    public const int FreeConnectionLimit = 1;

    /// <summary>
    /// The fallback network-hash key when none is configured: random, and shared by every
    /// instance of the service in this process so the counts at least last until a restart.
    /// </summary>
    private static readonly byte[] ProcessNetworkKey = RandomNumberGenerator.GetBytes(32);

    private readonly SpoolmanOptions _options = options.Value;

    public async Task<SpoolmanConnectionCreatedDto?> CreateAsync(long userId, long printerId, string requestOrigin, CancellationToken ct)
    {
        var printer = await context.Printers
            .Where(p => p.Id == printerId && p.UserId == userId)
            .Select(p => new { p.Id, p.Name })
            .SingleOrDefaultAsync(ct);
        if (printer is null)
        {
            return null;
        }

        if (!await IsProAsync(userId, ct))
        {
            // This printer's own connection does not count: reconnecting it is a rotation, which
            // must work on Free or a user who lost their secret could never recover.
            var activeElsewhere = await context.SpoolmanConnections.CountAsync(c =>
                c.UserId == userId && c.RevokedAt == null && c.DisabledAt == null && c.PrinterId != printerId, ct);
            if (activeElsewhere >= FreeConnectionLimit)
            {
                telemetry.TrackEvent("SpoolmanConnection_LimitReached");
                throw new SpoolmanConnectionLimitException(FreeConnectionLimit);
            }
        }

        var secret = SpoolmanSecret.Generate();
        var now = clock.GetUtcNow().UtcDateTime;
        var connection = new SpoolmanConnection
        {
            UserId = userId,
            PrinterId = printer.Id,
            Name = Truncate(string.IsNullOrWhiteSpace(printer.Name) ? $"Printer {printer.Id}" : printer.Name.Trim(), 100),
            SecretHash = SpoolmanSecret.Hash(secret),
            HashAlgorithm = SpoolmanSecret.HashAlgorithm,
            CreatedDate = now,
        };

        // Revoke-then-insert as one unit: the filtered unique index allows one unrevoked
        // connection per printer, so the insert must see the revocation, and a failure must not
        // leave the printer with neither. The execution strategy wraps it because SQL Server's
        // retrying strategy refuses a user transaction outside one.
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            await context.SpoolmanConnections
                .Where(c => c.PrinterId == printer.Id && c.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, now), ct);
            context.SpoolmanConnections.Add(connection);
            await context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        telemetry.TrackEvent("SpoolmanConnection_Created");

        var activeCount = await context.SpoolmanConnections
            .CountAsync(c => c.UserId == userId && c.RevokedAt == null && c.DisabledAt == null, ct);
        if (activeCount > _options.FairUseConnectionThreshold)
        {
            await AlertBestEffortAsync(userId, SpoolmanFairUseAlert.ConnectionCountSignal,
                $"More than {_options.FairUseConnectionThreshold} active Spoolman connections.");
        }

        var serverUrl = ServerUrl(requestOrigin, printer.Id, secret);
        var created = new SpoolmanConnectionCreatedDto
        {
            Secret = secret,
            ServerUrl = serverUrl,
            ConfigBlock = $"[spoolman]\nserver: {serverUrl}\n",
        };
        Fill(created, connection, printer.Name);
        return created;
    }

    public async Task<IReadOnlyList<SpoolmanConnectionDto>> ListAsync(long userId, CancellationToken ct)
    {
        var rows = await context.SpoolmanConnections
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedDate)
            .Select(c => new { Connection = c, PrinterName = c.Printer != null ? c.Printer.Name : null })
            .ToListAsync(ct);

        return rows.Select(r => Fill(new SpoolmanConnectionDto(), r.Connection, r.PrinterName)).ToList();
    }

    public async Task<bool> RevokeAsync(long userId, Guid connectionId, CancellationToken ct)
    {
        var connection = await context.SpoolmanConnections
            .SingleOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, ct);
        if (connection is null)
        {
            return false;
        }

        if (connection.RevokedAt is null)
        {
            connection.RevokedAt = clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(ct);
            telemetry.TrackEvent("SpoolmanConnection_Revoked");
        }

        return true;
    }

    public async Task<IReadOnlyList<Guid>> ApplyTierAsync(long userId, CancellationToken ct)
    {
        var live = await context.SpoolmanConnections
            .Where(c => c.UserId == userId && c.RevokedAt == null)
            .OrderBy(c => c.CreatedDate).ThenBy(c => c.Id)
            .ToListAsync(ct);

        var changed = new List<Guid>();
        if (await IsProAsync(userId, ct))
        {
            foreach (var connection in live.Where(c => c.DisabledReason == SpoolmanConnection.TierDisabledReason))
            {
                connection.DisabledAt = null;
                connection.DisabledReason = null;
                changed.Add(connection.Id);
            }
        }
        else
        {
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var connection in live.Where(c => c.DisabledAt == null).Skip(FreeConnectionLimit))
            {
                connection.DisabledAt = now;
                connection.DisabledReason = SpoolmanConnection.TierDisabledReason;
                changed.Add(connection.Id);
            }
        }

        if (changed.Count > 0)
        {
            await context.SaveChangesAsync(ct);
            telemetry.TrackEvent("SpoolmanConnection_TierApplied", new Dictionary<string, string>
            {
                ["changed"] = changed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        return changed;
    }

    public async Task RecordNetworkAsync(long userId, IPAddress address, CancellationToken ct)
    {
        var hash = SpoolmanNetwork.Hash(address, NetworkKey());
        var now = clock.GetUtcNow().UtcDateTime;

        var seen = await context.SpoolmanNetworkSightings
            .Where(s => s.UserId == userId && s.NetworkHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now), ct);
        if (seen == 0)
        {
            context.SpoolmanNetworkSightings.Add(new SpoolmanNetworkSighting
            {
                UserId = userId,
                NetworkHash = hash,
                FirstSeenAt = now,
                LastSeenAt = now,
            });
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent handshake from the same network inserted it first. Either row
                // records the same fact.
                context.ChangeTracker.Clear();
            }
        }

        var expired = now - SpoolmanNetworkSighting.Retention;
        await context.SpoolmanNetworkSightings
            .Where(s => s.UserId == userId && s.LastSeenAt < expired)
            .ExecuteDeleteAsync(ct);

        var weekAgo = now - TimeSpan.FromDays(7);
        var networks = await context.SpoolmanNetworkSightings
            .CountAsync(s => s.UserId == userId && s.LastSeenAt >= weekAgo, ct);
        if (networks > _options.FairUseNetworkThreshold)
        {
            await AlertBestEffortAsync(userId, SpoolmanFairUseAlert.NetworkCountSignal,
                $"Spoolman connections from more than {_options.FairUseNetworkThreshold} networks in the last 7 days.");
        }
    }

    private Task<bool> IsProAsync(long userId, CancellationToken ct) =>
        context.Subscriptions.AnyAsync(s => s.UserId == userId && s.Status == SubscriptionStatus.Active, ct);

    private byte[] NetworkKey()
    {
        if (string.IsNullOrWhiteSpace(_options.NetworkHashKey))
        {
            return ProcessNetworkKey;
        }

        try
        {
            return Convert.FromBase64String(_options.NetworkHashKey);
        }
        catch (FormatException)
        {
            logger.LogWarning("Spoolman:NetworkHashKey is not base64; using a per-process key.");
            return ProcessNetworkKey;
        }
    }

    /// <summary>
    /// Emails the admin about a fair-use signal, at most once per user per signal per week.
    /// Never blocks anything, and never fails the caller: the connection is already made, and a
    /// mail outage must not turn into a failed connect.
    ///
    /// The cool-down is claimed before sending, with a conditional update or a unique insert, so
    /// two requests crossing the threshold together send one email between them.
    /// </summary>
    private async Task AlertBestEffortAsync(long userId, string signal, string reason)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminEmailAddress))
        {
            return;
        }

        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var cutoff = now - SpoolmanFairUseAlert.Cooldown;

            var claimed = await context.SpoolmanFairUseAlerts
                .Where(a => a.UserId == userId && a.Signal == signal && a.LastSentAt <= cutoff)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSentAt, now), CancellationToken.None) > 0;
            if (!claimed)
            {
                if (await context.SpoolmanFairUseAlerts.AnyAsync(a => a.UserId == userId && a.Signal == signal, CancellationToken.None))
                {
                    return; // inside the cool-down
                }

                context.SpoolmanFairUseAlerts.Add(new SpoolmanFairUseAlert { UserId = userId, Signal = signal, LastSentAt = now });
                try
                {
                    await context.SaveChangesAsync(CancellationToken.None);
                }
                catch (DbUpdateException)
                {
                    context.ChangeTracker.Clear();
                    return; // another request claimed it first
                }
            }

            var weekAgo = now - TimeSpan.FromDays(7);
            var activeConnections = await context.SpoolmanConnections
                .CountAsync(c => c.UserId == userId && c.RevokedAt == null && c.DisabledAt == null, CancellationToken.None);
            var networks = await context.SpoolmanNetworkSightings
                .CountAsync(s => s.UserId == userId && s.LastSeenAt >= weekAgo, CancellationToken.None);
            var isPro = await IsProAsync(userId, CancellationToken.None);
            var webBase = emailOptions.Value.WebBaseUrl?.TrimEnd('/');

            var body = $@"Spoolman fair-use signal: {SecurityElement.Escape(reason)}<br>
<br>
User id: {userId}<br>
Plan: {(isPro ? "Pro" : "Free")}<br>
Active Spoolman connections: {activeConnections}<br>
Distinct networks in the last 7 days: {networks}<br>
User: {SecurityElement.Escape($"{webBase}/users/{userId}")}<br>
<br>
Nothing has been blocked. Contact the account holder before capping anything.<br>
";
            await emailSender.SendEmailAsync(_options.AdminEmailAddress, "3D Print Log: Spoolman fair-use alert", body);
            telemetry.TrackEvent("SpoolmanFairUse_AlertSent", new Dictionary<string, string> { ["signal"] = signal });
        }
        catch (Exception ex)
        {
            telemetry.TrackEvent("SpoolmanFairUse_AlertFailed", new Dictionary<string, string> { ["signal"] = signal });
            telemetry.TrackException(ex);
        }
    }

    private string ServerUrl(string requestOrigin, long printerId, string secret)
    {
        var configured = string.IsNullOrWhiteSpace(_options.ServerUrl)
            ? requestOrigin.TrimEnd('/') + "/spoolman"
            : _options.ServerUrl;
        var uri = new Uri(configured);
        return $"{uri.Scheme}://printer-{printerId}:{secret}@{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static T Fill<T>(T dto, SpoolmanConnection connection, string? printerName) where T : SpoolmanConnectionDto
    {
        dto.Id = connection.Id;
        dto.PrinterId = connection.PrinterId;
        dto.PrinterName = connection.PrinterId.HasValue ? printerName : null;
        dto.Name = connection.Name;
        dto.CreatedDate = connection.CreatedDate;
        dto.LastSeenAt = connection.LastSeenAt;
        dto.DisabledAt = connection.DisabledAt;
        dto.DisabledReason = connection.DisabledReason;
        dto.RevokedAt = connection.RevokedAt;
        dto.State = connection.RevokedAt.HasValue
            ? SpoolmanConnectionDto.RevokedState
            : connection.DisabledAt.HasValue ? SpoolmanConnectionDto.DisabledState : SpoolmanConnectionDto.ActiveState;
        return dto;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
