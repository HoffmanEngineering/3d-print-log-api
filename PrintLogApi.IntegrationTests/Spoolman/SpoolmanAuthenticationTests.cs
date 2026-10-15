using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using PrintLogApi.Authentication;
using PrintLogApi.Authentication.Spoolman;
using PrintLogApi.IntegrationTests.Mcp;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.Spoolman;
using Xunit;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// The Basic-auth scheme for <c>/spoolman</c> (#176). Moonraker sends the userinfo of its
/// <c>[spoolman] server</c> URL as <c>Authorization: Basic</c>; the password is the connection's
/// secret and the username is cosmetic. Nothing else authenticates under <c>/spoolman</c>, and
/// the credential authenticates nothing outside it.
///
/// The Spoolman endpoints themselves arrive in #177 and #178, so these tests authenticate
/// against a test-only probe under <c>/spoolman/api/v1</c> that carries the same policy and
/// echoes the claims it received.
/// </summary>
public class SpoolmanAuthenticationTests(SpoolmanAuthenticationTests.Factory factory) : IClassFixture<SpoolmanAuthenticationTests.Factory>
{
    public const string ProbePath = "/spoolman/api/v1/__auth-probe";

    public class Factory : CustomWebApplicationFactory
    {
        public RecordingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Every category at Trace, so the credential test sees everything any configuration
            // could ever log, including the hosting layer's per-request lines.
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft"] = "Trace",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new SpoolmanProbeStartupFilter()));
        }
    }

    /// <summary>The same host with the failed-attempt limiter and the connector write budget switched on.</summary>
    public sealed class LimitedFactory : Factory
    {
        public const int MaxFailures = 3;
        public const int WriteBurst = 2;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spoolman:InvalidAttemptsPerMinute"] = MaxFailures.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Api:ConnectorWrites:PerMinute"] = "1",
                ["Api:ConnectorWrites:Burst"] = WriteBurst.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
        }
    }

    private readonly HttpClient _client = factory.CreateClient();
    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Accepting

    [Fact]
    public async Task AValidSecret_Authenticates_WithTheConnectionsClaims()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printer);

        var response = await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = await ReadClaimsAsync(response);
        Assert.Equal(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), claims["userId"]);
        Assert.Equal(printer.ToString(System.Globalization.CultureInfo.InvariantCulture), claims["printerId"]);
        Assert.Equal(connection.Id.ToString(), claims["connectionId"]);
    }

    [Fact]
    public async Task TheConnectorCredentialClaim_IsPresent_AndIsNotTheSecretOrItsStoredHash()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        var claims = await ReadClaimsAsync(await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!)));

        var credential = claims["connector"];
        Assert.StartsWith("spoolman:", credential);
        Assert.Equal(ConnectorCredentialClaim.ForSpoolman(connection.Secret!), credential);
        Assert.DoesNotContain(connection.Secret!, credential);
        Assert.DoesNotContain(SpoolmanSecret.Hash(connection.Secret!), credential, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheUsername_IsCosmetic()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        var response = await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!, user: "anything-at-all"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AValidSecret_AuthenticatesTheWebsocketHandshake()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        var response = await SendHandshakeAsync(connection.Secret!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Authenticating_StampsLastSeenAt()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!));

        var lastSeen = await WithDbAsync(db => db.SpoolmanConnections.Where(c => c.Id == connection.Id).Select(c => c.LastSeenAt).SingleAsync(Ct));
        Assert.NotNull(lastSeen);
    }

    #endregion

    #region Refusing

    [Fact]
    public async Task NoCredential_Is401_WithASpoolmanErrorBody_AndABasicChallenge()
    {
        var response = await SendAsync(HttpMethod.Get, ProbePath, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(response);
        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Basic", challenge.Scheme);
        // Not the REST resource-metadata pointer every other 401 carries: this is not OAuth.
        Assert.DoesNotContain("resource_metadata", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task AWrongSecret_Is401()
    {
        var response = await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic("not-a-real-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(response);
    }

    [Fact]
    public async Task AMalformedHeader_Is401()
    {
        var response = await SendAsync(HttpMethod.Get, ProbePath, new AuthenticationHeaderValue("Basic", "%%%not-base64%%%"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARevokedSecret_Is401()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!))).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>().RevokeAsync(user.Id, connection.Id, Ct);
        }

        // Refused at once, although the valid lookup was cached a moment ago: revoking evicts it.
        var response = await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(response);
    }

    [Fact]
    public async Task ARotatedSecret_Is401_AndTheNewOneWorks()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var old = await _support.ConnectAsync(user, printer);
        await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(old.Secret!));

        var rotated = await _support.ConnectAsync(user, printer);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(old.Secret!))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(rotated.Secret!))).StatusCode);
    }

    [Fact]
    public async Task ATierDisabledSecret_Is401_EvenWhenItsValidLookupWasCached()
    {
        var user = await _support.CreateUserAsync(pro: true);
        await _support.ConnectAsync(user);
        var newer = await _support.ConnectAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(newer.Secret!))).StatusCode);

        await _support.SetProAsync(user, false);
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>().ApplyTierAsync(user.Id, Ct);
        }

        var response = await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(newer.Secret!));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Free", (await ReadMessageAsync(response)));
    }

    [Fact]
    public async Task DeletingThePrinter_RefusesItsSecret()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printer);
        await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!));

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPrinterService>().DeletePrinter(printer);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(connection.Secret!))).StatusCode);
    }

    /// <summary>
    /// A secret matching no row is not cached (#193): a caller choosing random secrets would
    /// otherwise fill the shared cache budget. Proved by making the database say "valid" after
    /// the first refusal and seeing the second attempt reach it.
    /// </summary>
    [Fact]
    public async Task AnUnknownSecret_IsNotCached_SoTheSecondAttemptQueriesAgain()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var secret = SpoolmanSecret.Generate();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(secret))).StatusCode);

        await InsertConnectionAsync(user, printer, secret, revoked: false);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(secret))).StatusCode);
    }

    /// <summary>
    /// Moonraker retries a refused websocket every 2 seconds, forever. A revoked or paused
    /// connection's refusal is cached, so the second and every later attempt is answered without
    /// a query: proved here by making the database say "valid" behind the cache's back and seeing
    /// the refusal stand.
    /// </summary>
    [Fact]
    public async Task ARevokedSecret_IsRefusedFromTheCache_TheSecondTime()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var secret = SpoolmanSecret.Generate();
        var id = await InsertConnectionAsync(user, printer, secret, revoked: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(secret))).StatusCode);

        await WithDbAsync(db => db.SpoolmanConnections.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, (DateTime?)null), Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(secret))).StatusCode);
    }

    [Fact]
    public async Task ATierDisabledSecret_IsRefusedFromTheCache_TheSecondTime()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var secret = SpoolmanSecret.Generate();
        var id = await InsertConnectionAsync(user, printer, secret, revoked: false, disabled: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(secret))).StatusCode);

        await WithDbAsync(db => db.SpoolmanConnections.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DisabledAt, (DateTime?)null).SetProperty(c => c.DisabledReason, (string?)null), Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(secret))).StatusCode);
    }

    #endregion

    #region Only this credential, only here

    [Fact]
    public async Task AnInteractiveUser_IsRefusedUnderSpoolman()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ProbePath);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AJwt_IsRefusedUnderSpoolman()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ProbePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.Create(TestJwt.ApiAudience, IntegrationTestSeeder.TestUserOAuthId));

        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnApiKey_IsRefusedUnderSpoolman()
    {
        string apiKey;
        using (var scope = factory.Services.CreateScope())
        {
            apiKey = (await scope.ServiceProvider.GetRequiredService<IUserApiKeyService>()
                .GenerateNewApiKey(IntegrationTestSeeder.TestUserId, "spoolman-auth-test")).PublicKey!;
        }

        foreach (var request in new[]
        {
            WithHeader(new HttpRequestMessage(HttpMethod.Get, ProbePath), "X-Api-Key", apiKey),
            new HttpRequestMessage(HttpMethod.Get, $"{ProbePath}?api_key={apiKey}"),
            // An API key in the password slot is just a wrong secret.
            WithAuthorization(new HttpRequestMessage(HttpMethod.Get, ProbePath), SpoolmanTestSupport.Basic(apiKey)),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task ASpoolmanSecret_AuthenticatesNothingOutsideSpoolman()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Filaments");
        request.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownSpoolmanRoute_Is404_WithASpoolmanErrorBody()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        var response = await SendAsync(HttpMethod.Get, "/spoolman/api/v1/no-such-thing", SpoolmanTestSupport.Basic(connection.Secret!));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(response);
    }

    #endregion

    #region Handshake checks

    [Fact]
    public async Task TheHandshake_AppliesTheTier_RefusingAFreeUsersNewerConnection()
    {
        var user = await _support.CreateUserAsync(pro: true);
        var older = await _support.ConnectAsync(user);
        var newer = await _support.ConnectAsync(user);
        // Lapsed without the subscription path applying it, so authentication is the only defence.
        await _support.SetProAsync(user, false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendHandshakeAsync(newer.Secret!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendHandshakeAsync(older.Secret!)).StatusCode);

        var state = await WithDbAsync(db => db.SpoolmanConnections.SingleAsync(c => c.Id == newer.Id, Ct));
        Assert.Equal(SpoolmanConnection.TierDisabledReason, state.DisabledReason);
    }

    [Fact]
    public async Task TheHandshake_RecordsTheNetwork_AsAHash()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        await SendHandshakeAsync(connection.Secret!, IPAddress.Parse("203.0.113.40"));

        var sighting = await WithDbAsync(db => db.SpoolmanNetworkSightings.SingleAsync(s => s.UserId == user.Id, Ct));
        Assert.DoesNotContain("203.0.113", sighting.NetworkHash);
    }

    [Fact]
    public async Task APlainRequest_DoesNotRecordTheNetwork()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        await SendRawAsync(connection.Secret!, IPAddress.Parse("203.0.113.41"), handshake: false);

        Assert.Equal(0, await WithDbAsync(db => db.SpoolmanNetworkSightings.CountAsync(s => s.UserId == user.Id, Ct)));
    }

    #endregion

    #region Credentials never logged

    [Fact]
    public async Task Credentials_NeverReachLogsTelemetryOrMetrics()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        var secret = connection.Secret!;
        var wrong = SpoolmanSecret.Generate();
        var encoded = SpoolmanTestSupport.Basic(secret).Parameter!;
        var encodedWrong = SpoolmanTestSupport.Basic(wrong).Parameter!;

        await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(secret));
        await SendAsync(HttpMethod.Post, ProbePath, SpoolmanTestSupport.Basic(secret));
        await SendAsync(HttpMethod.Get, ProbePath, SpoolmanTestSupport.Basic(wrong));
        await SendAsync(HttpMethod.Get, "/spoolman/api/v1/no-such-thing", SpoolmanTestSupport.Basic(secret));
        await SendHandshakeAsync(secret);
        var metrics = await (await _client.GetAsync("/metrics", Ct)).Content.ReadAsStringAsync(Ct);

        var telemetry = string.Join("\n", factory.Telemetry.Items.Select(Describe));
        var logs = string.Join("\n", factory.Logs.Messages);
        foreach (var leaked in new[] { secret, wrong, encoded, encodedWrong })
        {
            Assert.DoesNotContain(leaked, telemetry);
            Assert.DoesNotContain(leaked, logs);
            Assert.DoesNotContain(leaked, metrics);
        }
        // The log capture is live. Request telemetry is not asserted present: the SDK's request
        // tracking hooks a process-wide listener, so which host's channel receives an item
        // depends on what else is running (see EmailControllerTests.Telemetry_NeverCarriesTheToken).
        Assert.Contains(factory.Logs.Messages, m => m.Contains(SpoolmanAuthenticationTests.ProbePath, StringComparison.Ordinal));
    }

    #endregion

    #region Helpers

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, AuthenticationHeaderValue? authorization)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = authorization;
        return _client.SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> SendHandshakeAsync(string secret, IPAddress? from = null)
    {
        var context = await SendRawAsync(secret, from ?? IPAddress.Parse("198.51.100.7"), handshake: true);
        return new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode);
    }

    /// <summary>
    /// Through the test server directly, which is the only way to set the peer address. A
    /// handshake is a GET carrying the upgrade headers; the probe answers it with 200 rather than
    /// upgrading, which is all the authentication path needs.
    /// </summary>
    private Task<HttpContext> SendRawAsync(string secret, IPAddress from, bool handshake) =>
        factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = ProbePath;
            context.Request.Headers.Authorization = SpoolmanTestSupport.Basic(secret).ToString();
            if (handshake)
            {
                context.Request.Headers.Connection = "Upgrade";
                context.Request.Headers.Upgrade = "websocket";
                context.Request.Headers.SecWebSocketVersion = "13";
                context.Request.Headers.SecWebSocketKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            }
            context.Connection.RemoteIpAddress = from;
        }, Ct);

    private async Task<Guid> InsertConnectionAsync(User user, long printerId, string secret, bool revoked, bool disabled = false)
    {
        return await WithDbAsync(async db =>
        {
            var connection = new SpoolmanConnection
            {
                UserId = user.Id,
                PrinterId = revoked ? null : printerId,
                Name = "Inserted",
                SecretHash = SpoolmanSecret.Hash(secret),
                HashAlgorithm = SpoolmanSecret.HashAlgorithm,
                CreatedDate = DateTime.UtcNow,
                RevokedAt = revoked ? DateTime.UtcNow : null,
                DisabledAt = disabled ? DateTime.UtcNow : null,
                DisabledReason = disabled ? SpoolmanConnection.TierDisabledReason : null,
            };
            db.SpoolmanConnections.Add(connection);
            await db.SaveChangesAsync(Ct);
            return connection.Id;
        });
    }

    private async Task<T> WithDbAsync<T>(Func<PrintLogContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
    }

    private static async Task<Dictionary<string, string>> ReadClaimsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(Ct))!;
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("message").GetString()!;
    }


    /// <summary>Every free-text field an Application Insights item can carry to the portal.</summary>
    private static string Describe(Microsoft.ApplicationInsights.Channel.ITelemetry item)
    {
        var parts = new List<string?> { item.GetType().Name, item.Context.User.AuthenticatedUserId, item.Context.Operation.Name };
        if (item is Microsoft.ApplicationInsights.DataContracts.ISupportProperties withProperties)
        {
            parts.AddRange(withProperties.Properties.Select(p => $"{p.Key}={p.Value}"));
        }
        parts.Add(item switch
        {
            Microsoft.ApplicationInsights.DataContracts.RequestTelemetry r => $"{r.Name} {r.Url} {r.ResponseCode}",
            Microsoft.ApplicationInsights.DataContracts.DependencyTelemetry d => $"{d.Name} {d.Data} {d.Target}",
            Microsoft.ApplicationInsights.DataContracts.TraceTelemetry t => t.Message,
            Microsoft.ApplicationInsights.DataContracts.ExceptionTelemetry e => $"{e.Message} {e.Exception}",
            Microsoft.ApplicationInsights.DataContracts.EventTelemetry e => e.Name,
            _ => null,
        });
        return string.Join(" | ", parts);
    }

    private static HttpRequestMessage WithHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Add(name, value);
        return request;
    }

    private static HttpRequestMessage WithAuthorization(HttpRequestMessage request, AuthenticationHeaderValue value)
    {
        request.Headers.Authorization = value;
        return request;
    }

    #endregion

    /// <summary>
    /// A test-only endpoint under <c>/spoolman/api/v1</c> behind the Spoolman policy, echoing the
    /// claims authentication produced. Mapped after the app's own pipeline, like the MCP probes.
    /// </summary>
    private sealed class SpoolmanProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMethods(ProbePath, [HttpMethods.Get, HttpMethods.Post], (HttpContext context) => Results.Json(new Dictionary<string, string?>
                {
                    ["userId"] = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                    ["printerId"] = context.User.FindFirst(SpoolmanAuthentication.PrinterIdClaim)?.Value,
                    ["connectionId"] = context.User.FindFirst(SpoolmanAuthentication.ConnectionIdClaim)?.Value,
                    ["connector"] = ConnectorCredentialClaim.Get(context.User),
                })).RequireAuthorization(SpoolmanAuthentication.Policy);
            });
        };
    }
}

/// <summary>The failed-attempt limiter and the connector write budget, on their own host.</summary>
public class SpoolmanAuthenticationLimitTests(SpoolmanAuthenticationTests.LimitedFactory factory)
    : IClassFixture<SpoolmanAuthenticationTests.LimitedFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RepeatedWrongSecrets_Are429_WithARoundedUpRetryAfter()
    {
        HttpResponseMessage? last = null;
        for (var i = 0; i <= SpoolmanAuthenticationTests.LimitedFactory.MaxFailures; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, SpoolmanAuthenticationTests.ProbePath);
            request.Headers.Authorization = SpoolmanTestSupport.Basic(SpoolmanSecret.Generate());
            last = await _client.SendAsync(request, Ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        var retryAfter = last.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value.TotalSeconds, 1, 60);
        Assert.Equal(0, retryAfter.Value.Milliseconds);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(last);
    }

    /// <summary>
    /// Behind a front end that collapses every caller into one address, one printer's typo must
    /// not lock out the others: only failing attempts are ever throttled.
    /// </summary>
    [Fact]
    public async Task AValidSecret_IsNeverThrottled_ByFailuresFromItsAddress()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        for (var i = 0; i <= SpoolmanAuthenticationTests.LimitedFactory.MaxFailures + 2; i++)
        {
            var wrong = new HttpRequestMessage(HttpMethod.Get, SpoolmanAuthenticationTests.ProbePath);
            wrong.Headers.Authorization = SpoolmanTestSupport.Basic(SpoolmanSecret.Generate());
            await _client.SendAsync(wrong, Ct);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, SpoolmanAuthenticationTests.ProbePath);
        request.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request, Ct)).StatusCode);
    }

    /// <summary>
    /// A burst of random secrets, past the failed-attempt limit, writes nothing to the cache
    /// (#193). The limiter answers after the lookup, so it was never what kept those entries
    /// out. A valid secret's entry is looked up the same way as a control, so a change to the
    /// key format fails here instead of passing vacuously.
    /// </summary>
    [Fact]
    public async Task ABurstOfUnknownSecrets_LeavesNoCacheEntries()
    {
        var unknown = Enumerable.Range(0, SpoolmanAuthenticationTests.LimitedFactory.MaxFailures + 5)
            .Select(_ => SpoolmanSecret.Generate())
            .ToList();
        var statuses = new List<HttpStatusCode>();
        foreach (var secret in unknown)
        {
            var wrong = new HttpRequestMessage(HttpMethod.Get, SpoolmanAuthenticationTests.ProbePath);
            wrong.Headers.Authorization = SpoolmanTestSupport.Basic(secret);
            statuses.Add((await _client.SendAsync(wrong, Ct)).StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);
        var valid = new HttpRequestMessage(HttpMethod.Get, SpoolmanAuthenticationTests.ProbePath);
        valid.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(valid, Ct)).StatusCode);

        Assert.NotNull(await PeekAsync(connection.Secret!));
        foreach (var secret in unknown)
        {
            Assert.Null(await PeekAsync(secret));
        }
    }

    /// <summary>Reads a credential's cache entry without computing or writing one.</summary>
    private async Task<SpoolmanCredential?> PeekAsync(string secret)
    {
        var cache = factory.Services.GetRequiredService<HybridCache>();
        return await cache.GetOrCreateAsync<SpoolmanCredential?>(
            $"spoolman_credential:{SpoolmanSecret.Hash(secret)}",
            _ => ValueTask.FromResult<SpoolmanCredential?>(null),
            new HybridCacheEntryOptions
            {
                Flags = HybridCacheEntryFlags.DisableUnderlyingData
                    | HybridCacheEntryFlags.DisableLocalCacheWrite
                    | HybridCacheEntryFlags.DisableDistributedCacheWrite,
            },
            cancellationToken: Ct);
    }

    [Fact]
    public async Task ConnectorWrites_AreBudgetedPerSpoolmanCredential()
    {
        var user = await _support.CreateUserAsync();
        var connection = await _support.ConnectAsync(user);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i <= SpoolmanAuthenticationTests.LimitedFactory.WriteBurst; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, SpoolmanAuthenticationTests.ProbePath);
            request.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);
            statuses.Add((await _client.SendAsync(request, Ct)).StatusCode);
        }

        Assert.All(statuses.Take(SpoolmanAuthenticationTests.LimitedFactory.WriteBurst), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        // Reads are never counted.
        var read = new HttpRequestMessage(HttpMethod.Get, SpoolmanAuthenticationTests.ProbePath);
        read.Headers.Authorization = SpoolmanTestSupport.Basic(connection.Secret!);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(read, Ct)).StatusCode);
    }
}

/// <summary>Captures every log message the host writes, at every level.</summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(RecordingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            provider._messages.Enqueue($"{category} {formatter(state, exception)} {structured} {exception}");
        }
    }
}
