using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Spoolman;
using PrintLogApi.Extensions;
using PrintLogApi.Models;

namespace PrintLogApi.Services.Spoolman;

/// <summary>
/// Holds the Spoolman websockets open (#178).
///
/// <para><b>Why a socket at all, in a read-only phase.</b> Moonraker counts Spoolman as connected
/// only while its socket to <c>/api/v1/spool</c> is open. Without one it sends no usage, marks
/// Spoolman disconnected, and its proxy answers Mainsail, Fluidd and KlipperScreen with 503.</para>
///
/// <para><b>What it does.</b> Accepts the handshake (authenticated by <c>SpoolmanBasic</c> like any
/// other request), sends <b>no events</b> (#172), answers any text message with
/// <c>{"status":"healthy"}</c> as Spoolman does, and leaves pings to the framework: Kestrel answers a
/// client's ping, and <c>UseWebSockets</c>' keep-alive pings an older Moonraker that never pings, well
/// inside Azure's 230-second idle timeout.</para>
///
/// <para><b>Re-checks.</b> Every <c>Spoolman:SocketRecheckSeconds</c> (default 300) each socket
/// re-applies its user's tier, so a lapsed subscription pauses extra printers even when the billing
/// path missed it, and re-resolves its credential through the cache, which revocation and pausing
/// evict. A socket whose connection is no longer valid is closed with 1008 (policy violation);
/// Moonraker's next handshake is then refused from the cache. The tick also stamps
/// <c>LastSeenAt</c>, so the web app shows a connected printer as recently seen.</para>
///
/// <para><b>Shutdown.</b> Sockets are closed with 1001 (going away) when the host stops, and a
/// peer that does not acknowledge within a few seconds is aborted so shutdown is not held. Moonraker
/// reconnects every 2 seconds and holds its usage in memory meanwhile, so a deploy loses nothing.</para>
///
/// <para><b>In-process, single instance.</b> The registry and gauge live in this process, which is
/// right for one App Service instance. Scaling out needs a backplane before #172's events can reach
/// a socket held by another instance.</para>
/// </summary>
public sealed class SpoolmanSocketHost(
    IServiceScopeFactory scopes,
    ISpoolmanCredentialCache credentials,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    TelemetryClient telemetry,
    ILogger<SpoolmanSocketHost> logger)
{
    private static readonly byte[] Healthy = Encoding.UTF8.GetBytes("""{"status":"healthy"}""");

    /// <summary>How long a peer has to acknowledge the shutdown close before its socket is aborted.</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<Guid, string> _open = new();

    /// <summary>Open sockets by tier, for the App Insights sampler.</summary>
    public (int Free, int Pro) Counts()
    {
        var tiers = _open.Values.ToList();
        return (tiers.Count(t => t == SpoolmanSocketMetrics.Free), tiers.Count(t => t == SpoolmanSocketMetrics.Pro));
    }

    /// <summary>Accepts the upgrade on an authenticated request and holds the socket until either side closes it.</summary>
    public async Task RunAsync(HttpContext context)
    {
        var userId = context.User.GetUserId() ?? throw new InvalidOperationException("The Spoolman policy guarantees a user.");
        var connectionId = Guid.Parse(context.User.FindFirst(SpoolmanAuthentication.ConnectionIdClaim)!.Value);
        var secretHash = context.Items[SpoolmanAuthentication.SecretHashItem] as string
            ?? throw new InvalidOperationException("SpoolmanBasic records the secret hash for the socket's re-checks.");

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        var sendLock = new SemaphoreSlim(1, 1);
        var socketId = Guid.NewGuid();

        var tier = await TierAsync(userId, CancellationToken.None);
        _open[socketId] = tier;
        SpoolmanSocketMetrics.OpenSockets.WithLabels(tier).Inc();
        telemetry.TrackEvent("Spoolman_SocketOpened", new Dictionary<string, string>
        {
            ["tier"] = tier,
            ["connectionId"] = connectionId.ToString(),
        });

        // The receive must not observe ApplicationStopping (#203). The server's socket is a
        // ManagedWebSocket, which registers Abort() on the token ReceiveAsync is given, so a
        // stopping token there aborted the socket before CloseAsync could send 1001 and Moonraker
        // saw 1006. Instead the stopping registration releases the close below, and the receive
        // ends when the peer acknowledges it, or when the grace runs out and its token aborts it.
        using var receive = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var goingAway = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = lifetime.ApplicationStopping.Register(() => goingAway.TrySetResult());

        var recheck = RecheckLoopAsync(socket, sendLock, socketId, userId, connectionId, secretHash, stop.Token);
        try
        {
            var receiving = ReceiveLoopAsync(socket, sendLock, receive.Token);
            if (await Task.WhenAny(receiving, goingAway.Task) == goingAway.Task)
            {
                // Start the grace before taking sendLock: a Healthy send stuck on a peer that stopped
                // reading holds the lock, and only cancelling receive frees it.
                receive.CancelAfter(CloseGrace);
                await CloseAsync(socket, sendLock, WebSocketCloseStatus.EndpointUnavailable, "Server restarting");
            }

            await receiving;
        }
        catch (OperationCanceledException)
        {
            // The client went away, or never acknowledged the shutdown close; nothing more to send.
        }
        catch (WebSocketException)
        {
            // The client dropped without a close frame, which is how a printer losing power ends.
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await recheck;
            }
            catch (OperationCanceledException)
            {
            }

            if (_open.TryRemove(socketId, out var finalTier))
            {
                SpoolmanSocketMetrics.OpenSockets.WithLabels(finalTier).Dec();
            }
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, SemaphoreSlim sendLock, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(socket, sendLock, WebSocketCloseStatus.NormalClosure, null);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
            {
                await sendLock.WaitAsync(ct);
                try
                {
                    await socket.SendAsync(Healthy, WebSocketMessageType.Text, endOfMessage: true, ct);
                }
                finally
                {
                    sendLock.Release();
                }
            }
        }
    }

    private async Task RecheckLoopAsync(
        WebSocket socket, SemaphoreSlim sendLock, Guid socketId, long userId, Guid connectionId, string secretHash, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Spoolman:SocketRecheckSeconds", 300)));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var services = scope.ServiceProvider;

                // Re-applying the tier evicts any credential it pauses, so the resolve below sees it.
                await services.GetRequiredService<ISpoolmanConnectionService>().ApplyTierAsync(userId, ct);
                var credential = await credentials.ResolveAsync(secretHash, ct);
                if (credential.Status != SpoolmanCredentialStatus.Valid)
                {
                    telemetry.TrackEvent("Spoolman_SocketClosedByRecheck", new Dictionary<string, string> { ["status"] = credential.Status.ToString() });
                    await CloseAsync(socket, sendLock, WebSocketCloseStatus.PolicyViolation,
                        credential.Status == SpoolmanCredentialStatus.Disabled ? "Connection paused" : "Connection revoked");
                    return;
                }

                var now = DateTime.UtcNow;
                await services.GetRequiredService<PrintLogContext>().SpoolmanConnections
                    .Where(c => c.Id == connectionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeenAt, now), ct);

                var tier = await TierAsync(userId, ct, services);
                if (_open.TryGetValue(socketId, out var previous) && previous != tier && _open.TryUpdate(socketId, tier, previous))
                {
                    SpoolmanSocketMetrics.OpenSockets.WithLabels(previous).Dec();
                    SpoolmanSocketMetrics.OpenSockets.WithLabels(tier).Inc();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A database blip must not drop a healthy printer; try again next tick.
                logger.LogWarning(ex, "Spoolman socket re-check failed; keeping the socket open.");
            }
        }
    }

    private async Task<string> TierAsync(long userId, CancellationToken ct, IServiceProvider? services = null)
    {
        await using var scope = services is null ? scopes.CreateAsyncScope() : (AsyncServiceScope?)null;
        var db = (services ?? scope!.Value.ServiceProvider).GetRequiredService<PrintLogContext>();
        var pro = await db.Subscriptions.AnyAsync(s => s.UserId == userId && s.Status == SubscriptionStatus.Active, ct);
        return pro ? SpoolmanSocketMetrics.Pro : SpoolmanSocketMetrics.Free;
    }

    private static async Task CloseAsync(WebSocket socket, SemaphoreSlim sendLock, WebSocketCloseStatus status, string? reason)
    {
        await sendLock.WaitAsync(CancellationToken.None);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Already gone.
        }
        finally
        {
            sendLock.Release();
        }
    }
}

/// <summary>
/// Samples open Spoolman sockets by tier into Application Insights once a minute, for the
/// workbook's "open sockets over time" chart. The Prometheus gauge has the live value; App Insights
/// is where the workbook reads history from.
/// </summary>
public sealed class SpoolmanSocketSampler(SpoolmanSocketHost host, TelemetryClient telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var (free, pro) = host.Counts();
                var metric = telemetry.GetMetric("Spoolman_OpenSockets", "tier");
                metric.TrackValue(free, SpoolmanSocketMetrics.Free);
                metric.TrackValue(pro, SpoolmanSocketMetrics.Pro);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
