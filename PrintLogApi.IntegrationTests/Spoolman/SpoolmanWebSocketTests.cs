using System.Net.WebSockets;
using System.Text;
using PrintLogApi.Services.Spoolman;
using Xunit;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// The Spoolman websocket (#178). Moonraker treats Spoolman as connected only while its socket to
/// <c>/api/v1/spool</c> is open: it sends no usage, and its proxy answers Mainsail, Fluidd and
/// KlipperScreen with 503, until it is. Phase 1 sends no events, so the socket's whole job is to
/// accept the right credential, answer pings and text, and close when the credential stops being
/// valid or the server stops.
/// </summary>
public class SpoolmanWebSocketTests(SpoolmanWebSocketTests.Factory factory) : IClassFixture<SpoolmanWebSocketTests.Factory>
{
    /// <summary>Re-checks open sockets every second, so revocation is observable inside a test.</summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spoolman:SocketRecheckSeconds"] = "1",
            }));
        }
    }

    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/spoolman/api/v1/spool")] // Moonraker
    [InlineData("/spoolman/api/v1/")] // Fluidd, spoolman2slicer
    [InlineData("/spoolman/api/v1/spool/1")]
    public async Task AValidSecret_OpensTheSocket(string path)
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        using var socket = await ConnectAsync(connection.Secret!, path);

        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
    }

    [Fact]
    public async Task AWrongSecret_IsRefusedAtTheHandshake()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(SpoolmanSecret.Generate(), "/spoolman/api/v1/spool"));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task NoCredential_IsRefusedAtTheHandshake()
    {
        var client = factory.Server.CreateWebSocketClient();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri(factory.Server.BaseAddress, "/spoolman/api/v1/spool"), Ct));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task ATextMessage_IsAnsweredHealthy_AsSpoolmanDoes()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        using var socket = await ConnectAsync(connection.Secret!, "/spoolman/api/v1/spool");

        await socket.SendAsync(Encoding.UTF8.GetBytes("ping?"), WebSocketMessageType.Text, true, Ct);
        var reply = await ReceiveTextAsync(socket);

        Assert.Equal("""{"status":"healthy"}""", reply);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
    }

    [Fact]
    public async Task TheSocket_Closes_WhenTheConnectionIsRevoked()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        using var socket = await ConnectAsync(connection.Secret!, "/spoolman/api/v1/spool");

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>().RevokeAsync(user.Id, connection.Id, Ct);
        }

        var result = await ReceiveUntilCloseAsync(socket, TimeSpan.FromSeconds(15));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result);
    }

    [Fact]
    public async Task TheSocket_Closes_WhenADowngradePausesItsConnection()
    {
        var user = await _support.CreateUserAsync(pro: true);
        await _support.ConnectAsync(user);
        var newer = await _support.ConnectAsync(user);
        using var socket = await ConnectAsync(newer.Secret!, "/spoolman/api/v1/spool");

        // Lapsed, with nothing applying the tier: the timer's Pro re-check has to notice.
        await _support.SetProAsync(user, false);

        var result = await ReceiveUntilCloseAsync(socket, TimeSpan.FromSeconds(15));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, result);
    }

    [Fact]
    public async Task TheGauge_CountsOpenSocketsByTier()
    {
        var free = await _support.CreateUserAsync();
        var freeConnection = await _support.ConnectAsync(free);
        var pro = await _support.CreateUserAsync(pro: true);
        var proConnection = await _support.ConnectAsync(pro);
        var freeBefore = SpoolmanSocketMetrics.OpenSockets.WithLabels("free").Value;
        var proBefore = SpoolmanSocketMetrics.OpenSockets.WithLabels("pro").Value;

        using var freeSocket = await ConnectAsync(freeConnection.Secret!, "/spoolman/api/v1/spool");
        using var proSocket = await ConnectAsync(proConnection.Secret!, "/spoolman/api/v1/spool");
        await WaitForAsync(() => SpoolmanSocketMetrics.OpenSockets.WithLabels("free").Value >= freeBefore + 1
            && SpoolmanSocketMetrics.OpenSockets.WithLabels("pro").Value >= proBefore + 1);

        await freeSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
        await proSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
        await WaitForAsync(() => SpoolmanSocketMetrics.OpenSockets.WithLabels("free").Value <= freeBefore
            && SpoolmanSocketMetrics.OpenSockets.WithLabels("pro").Value <= proBefore);
    }

    [Fact]
    public async Task TheMetrics_ExposeTheGaugeWithATierLabelAndNoUser()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        using var socket = await ConnectAsync(connection.Secret!, "/spoolman/api/v1/spool");

        var metrics = await factory.CreateClient().GetStringAsync("/metrics", Ct);

        var lines = metrics.Split('\n').Where(l => l.StartsWith("printlog_spoolman_open_sockets{", StringComparison.Ordinal)).ToList();
        Assert.Contains(lines, l => l.Contains("tier=\"free\"", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain("user", l, StringComparison.OrdinalIgnoreCase));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", Ct);
    }

    [Fact]
    public async Task PlainRequests_ToTheSocketPaths_StillServeRest()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        var request = new HttpRequestMessage(HttpMethod.Get, "/spoolman/api/v1/spool");
        request.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);

        var response = await factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<WebSocket> ConnectAsync(string secret, string path)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = SpoolmanTestSupport.Basic(secret).ToString();
        return await client.ConnectAsync(new Uri(factory.Server.BaseAddress, path), Ct);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static async Task<WebSocketCloseStatus?> ReceiveUntilCloseAsync(WebSocket socket, TimeSpan within)
    {
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(within);
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return result.CloseStatus;
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }
        Assert.True(condition());
    }
}

/// <summary>A server shutting down closes its Spoolman sockets, so Moonraker reconnects promptly.</summary>
public class SpoolmanWebSocketShutdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stopping_ClosesOpenSockets_AsGoingAway()
    {
        using var factory = new CustomWebApplicationFactory();
        var support = new SpoolmanTestSupport(factory.Services);
        var user = await support.CreateUserAsync();
        var connection = await support.ConnectAsync(user);
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!).ToString();
        using var socket = await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "/spoolman/api/v1/spool"), Ct);

        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        var buffer = new byte[1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, result.CloseStatus);
    }
}
