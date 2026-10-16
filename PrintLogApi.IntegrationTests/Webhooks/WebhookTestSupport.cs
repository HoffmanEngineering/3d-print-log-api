using System.Net;
using System.Net.Sockets;
using System.Text;
using PrintLogApi.Webhooks;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>DNS answers chosen by the test. An unmapped host does not resolve.</summary>
public sealed class FakeWebhookHostResolver : IWebhookHostResolver
{
    private readonly Dictionary<string, IPAddress[]> _answers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every host not mapped resolves to this when set.</summary>
    public IPAddress[]? Default { get; set; }

    public FakeWebhookHostResolver Map(string host, params string[] addresses)
    {
        _answers[host] = addresses.Select(IPAddress.Parse).ToArray();
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (_answers.TryGetValue(host, out var answer) || (answer = Default) is not null)
        {
            return Task.FromResult(answer);
        }
        throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>
/// A bare HTTP/1.1 server on a loopback port, so the connect-time guard is tested against a real
/// socket rather than a stub handler. Counts the connections it accepted.
/// </summary>
public sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _connections;

    public LoopbackHttpServer(string response)
    {
        _listener.Start();
        _loop = Task.Run(() => Serve(response));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref _connections);

    public static string Ok(string body = "ok") =>
        $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private async Task Serve(string response)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var received = new StringBuilder();
                    while (!received.ToString().Contains("\r\n\r\n"))
                    {
                        var n = await stream.ReadAsync(buffer, _stop.Token);
                        if (n == 0) break;
                        received.Append(Encoding.ASCII.GetString(buffer, 0, n));
                    }
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
                catch
                {
                    // The client gave up; nothing to assert here.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch { }
        _stop.Dispose();
    }
}
