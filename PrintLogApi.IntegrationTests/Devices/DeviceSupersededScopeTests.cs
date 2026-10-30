using System.Net;
using System.Text.Json;
using PrintLogApi.Authentication.Devices;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// What a token a re-pair replaced may still do during its grace (api#287): log prints and send
/// heartbeats, nothing else, enforced in <see cref="DeviceTokenMiddleware"/> for every endpoint.
/// The walk below pins it the way <see cref="DeviceAccessCoverageTests"/> pins the allow-list.
/// </summary>
public partial class DeviceSupersededScopeTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    /// <summary>
    /// Every endpoint a superseded token still reaches. Exactly the prints:write and
    /// connections:write rows of the allow-list; adding one is a security decision.
    /// </summary>
    private static readonly SortedSet<string> ExpectedDuringGrace = new(StringComparer.Ordinal)
    {
        "POST api/Prints prints:write",
        "PATCH api/Prints/{id} prints:write",
        "POST api/Prints/{id}/claim prints:write",
        "POST api/Prints/{id}/complete prints:write",
        "POST api/Prints/{id}/image prints:write",
        "POST api/Prints/{id}/slicer-settings prints:write",
        "PUT api/Connections/{instanceId} connections:write",
    };

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, Ct);

    private async Task<(string Subject, string Old, string Current)> RepairedAsync(string label)
    {
        var subject = NewSubject(label);
        var deviceId = Guid.NewGuid();
        var old = (await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId)).GetProperty("token").GetString()!;
        var current = (await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId)).GetProperty("token").GetString()!;
        return (subject, old, current);
    }

    [Fact]
    public async Task ASupersededFullScopeToken_ReachesOnlyPrintWritesAndHeartbeats()
    {
        var (_, old, _) = await RepairedAsync("superseded-walk");
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<DeviceAccessAttribute>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (Endpoint: e, Method: m)))
            .ToList();

        var reached = new SortedSet<string>(StringComparer.Ordinal);
        var wrongRefusals = new List<string>();
        foreach (var (endpoint, method) in endpoints)
        {
            var url = "/" + Parameter().Replace(endpoint.RoutePattern.RawText!.TrimStart('/'), _ => "1");
            var request = WithDevice(new HttpRequestMessage(new HttpMethod(method), url), old);
            if (method is "POST" or "PUT" or "PATCH")
            {
                var consumes = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>()?.ContentTypes ?? [];
                request.Content = consumes.Any(c => c.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                    ? new MultipartFormDataContent { { new StringContent("x"), "unused" } }
                    : JsonContent.Create(new { });
            }

            var response = await Send(request);
            var code = await CodeOf(response);
            var key = $"{method} {endpoint.RoutePattern.RawText!.TrimStart('/')} {endpoint.Metadata.GetMetadata<DeviceAccessAttribute>()!.Scope ?? "*"}";
            if (code == DeviceTokenErrors.TokenSuperseded)
            {
                if (response.StatusCode != HttpStatusCode.Forbidden)
                {
                    wrongRefusals.Add($"{key}: {(int)response.StatusCode}");
                }
            }
            else if (response.StatusCode is not HttpStatusCode.Unauthorized && code is not (DeviceTokenErrors.EndpointNotAllowed or DeviceTokenErrors.InsufficientScope))
            {
                reached.Add(key);
            }
            else
            {
                wrongRefusals.Add($"{key}: {(int)response.StatusCode} {code}");
            }
        }

        Assert.Empty(wrongRefusals);
        Assert.Equal(ExpectedDuringGrace, reached);
    }

    [Fact]
    public async Task ASupersededToken_CannotReadPrintsOrCreatePrinters()
    {
        var (_, old, current) = await RepairedAsync("superseded-read");

        var read = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Prints/claimable"), old));
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(DeviceTokenErrors.TokenSuperseded, await CodeOf(read));

        var me = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), old));
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        Assert.Equal(DeviceTokenErrors.TokenSuperseded, await CodeOf(me));

        // The current token is not affected.
        Assert.Equal(HttpStatusCode.OK, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), current))).StatusCode);
    }

    /// <summary>
    /// A heartbeat with a superseded token is accepted but never changes which bridge a
    /// connection belongs to: an existing link stays (the real bridge may still hold only the old
    /// token), and a connection it creates gets none, so a copy of the old token cannot attach
    /// connections to the bridge.
    /// </summary>
    [Fact]
    public async Task ASupersededHeartbeat_KeepsAnExistingBridgeLink_AndRecordsNoNewOne()
    {
        var subject = NewSubject("superseded-heartbeat");
        var deviceId = Guid.NewGuid();
        var old = (await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId)).GetProperty("token").GetString()!;
        await Bridges.BridgeTestKit.CheckInOkAsync(_client, old, Bridges.BridgeTestKit.CheckInBody());
        var printerId = await CreatePrinterAsync(_client, subject);
        var linked = Guid.NewGuid().ToString();
        HttpRequestMessage Heartbeat(string instanceId, string token) => WithDevice(new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}")
        {
            Content = JsonContent.Create(new { kind = "moonraker", displayName = "voron", printerId }),
        }, token);

        var first = await Send(Heartbeat(linked, old));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var bridgeId = (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("bridgeId").GetInt64();

        await PairAsync(_client, subject, scopes: DeviceScopes.All, deviceId: deviceId);

        var again = await Send(Heartbeat(linked, old));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(bridgeId, (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("bridgeId").GetInt64());

        var fresh = await Send(Heartbeat(Guid.NewGuid().ToString(), old));
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await fresh.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("bridgeId").ValueKind);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{\**([^}]+)\}")]
    private static partial System.Text.RegularExpressions.Regex Parameter();
}
