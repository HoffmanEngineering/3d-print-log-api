using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Spoolman;
using PrintLogApi.Services;
using PrintLogApi.Services.Spoolman;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Spoolman connection tokens (#175): one per printer, minted only by an interactive user,
/// shown once, Free-tier limited to one active connection, paused (never deleted) by a
/// downgrade, and watched for fair use by an admin email that never blocks anything.
///
/// Each test arranges a fresh user: tier rules and fair-use counts are per user, and the
/// fixture's database is shared with every sibling.
/// </summary>
public class SpoolmanConnectionsTests(SpoolmanConnectionsTests.Factory factory) : IClassFixture<SpoolmanConnectionsTests.Factory>
{
    public const string AdminAddress = "spoolman-admin@example.test";
    private const string WebhookSecret = "whsec_spoolman_connection_tests";

    public sealed class Factory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spoolman:ServerUrl"] = "https://api.3dprintlog.test/spoolman",
                ["Spoolman:AdminEmailAddress"] = AdminAddress,
                ["Spoolman:NetworkHashKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("spoolman-network-hash-test-key-0123456789")),
                ["Stripe:WebhookSecret"] = WebhookSecret,
            }));
        }
    }

    private readonly HttpClient _client = factory.CreateClient();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region Creating

    [Fact]
    public async Task Create_ReturnsTheSecretAndAReadyToPasteBlock()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user, "Voron 2.4");

        var created = await CreateAsync(user, printer);

        Assert.Equal(printer, created.PrinterId);
        Assert.Equal("Voron 2.4", created.Name);
        Assert.Equal("active", created.State);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43,}$"), created.Secret);
        Assert.Equal(
            ExpectedBlock($"https://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman",
                $"jsons://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman/notifier"),
            created.ConfigBlock);
    }

    [Fact]
    public async Task ConfigBlock_IncludesTheNotifier_OverHttps()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);

        var created = await CreateAsync(user, printer);

        Assert.Contains(
            $"url: jsons://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman/notifier\n",
            created.ConfigBlock);
        Assert.Equal(
            ExpectedBlock($"https://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman",
                $"jsons://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman/notifier"),
            created.ConfigBlock);
    }

    [Fact]
    public async Task ConfigBlock_UsesJsonAndKeepsThePort_OverHttp()
    {
        await using var httpFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spoolman:ServerUrl"] = "http://192.168.4.65:5100/spoolman",
            })));
        using var client = httpFactory.CreateClient();

        long printer;
        string oauthId;
        using (var scope = httpFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = new User
            {
                OAuthUserId = $"auth0|spoolman-{Guid.NewGuid():N}",
                DisplayName = "Spoolman",
                ViewStatus = User.ProfileViewStatus.Private,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);
            var p = new Printer { Name = "Http printer", UserId = user.Id, IsActive = true, CategoryNickname = "FDM" };
            db.Printers.Add(p);
            await db.SaveChangesAsync(Ct);
            printer = p.Id;
            oauthId = user.OAuthUserId;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Printers/{printer}/spoolman-connection");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId);
        var response = await client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var created = (await response.Content.ReadFromJsonAsync<SpoolmanConnectionCreatedDto>(Ct))!;

        Assert.Equal(
            ExpectedBlock($"http://printer-{printer}:{created.Secret}@192.168.4.65:5100/spoolman",
                $"json://printer-{printer}:{created.Secret}@192.168.4.65:5100/spoolman/notifier"),
            created.ConfigBlock);
    }

    private static string ExpectedBlock(string server, string notifierUrl) =>
        $"[spoolman]\nserver: {server}\n\n"
        + "[notifier 3d_print_log]\n"
        + $"url: {notifierUrl}\n"
        + "events: started, complete, error, cancelled\n"
        + "body:\n"
        + "    {% set payload = {\"event_name\": event_name} %}\n"
        + "    {% do payload.update(event_args[1]) %}\n"
        + "    { payload|tojson }\n";

    private static string ServerOnlyBlock(string server) => $"[spoolman]\nserver: {server}\n";

    [Fact]
    public async Task ConfigBlock_ForAPrinterWithABridge_LeavesOutTheNotifier()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);
        await AddConnectionAsync(user, printer, Connection.MoonrakerKind, DateTime.UtcNow);

        var created = await CreateAsync(user, printer);

        Assert.Equal(
            ServerOnlyBlock($"https://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman"),
            created.ConfigBlock);
        Assert.DoesNotContain("[notifier", created.ConfigBlock);
        Assert.Equal($"https://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman", created.ServerUrl);
    }

    [Fact]
    public async Task ConfigBlock_ForAPrinterWithAStaleBridge_StillLeavesOutTheNotifier()
    {
        // The block is pasted once and kept, so it follows whether the printer runs the bridge,
        // not whether the bridge happened to be up when the user clicked Connect.
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);
        await AddConnectionAsync(user, printer, Connection.MoonrakerKind, DateTime.UtcNow - TimeSpan.FromDays(3));

        var created = await CreateAsync(user, printer);

        Assert.Contains("[spoolman]\n", created.ConfigBlock);
        Assert.DoesNotContain("[notifier", created.ConfigBlock);
    }

    [Fact]
    public async Task ConfigBlock_KeepsTheNotifier_WhenTheBridgeIsOnAnotherPrinterOrIsNotMoonraker()
    {
        var user = await CreateUserAsync(pro: true);
        var printer = await CreatePrinterAsync(user);
        var other = await CreatePrinterAsync(user);
        await AddConnectionAsync(user, other, Connection.MoonrakerKind, DateTime.UtcNow);
        await AddConnectionAsync(user, printer, "octoprint", DateTime.UtcNow);
        await AddConnectionAsync(user, null, Connection.MoonrakerKind, DateTime.UtcNow);

        var created = await CreateAsync(user, printer);

        Assert.Equal(
            ExpectedBlock($"https://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman",
                $"jsons://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman/notifier"),
            created.ConfigBlock);
    }

    [Fact]
    public async Task ConfigBlock_KeepsTheNotifier_AfterTheBridgeConnectionIsRemoved()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);
        var instanceId = await AddConnectionAsync(user, printer, Connection.MoonrakerKind, DateTime.UtcNow);
        Assert.DoesNotContain("[notifier", (await CreateAsync(user, printer)).ConfigBlock);

        var deleted = await SendAsync(HttpMethod.Delete, $"/api/Connections/{instanceId}", user);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var created = await CreateAsync(user, printer);

        Assert.Contains("[spoolman]\n", created.ConfigBlock);
        Assert.Contains("[notifier 3d_print_log]\n", created.ConfigBlock);
    }

    [Theory]
    [InlineData("Snapmaker", "U1")]
    [InlineData("snapmaker", "Snapmaker U1")]
    [InlineData("Snapmaker ", "u1 ")]
    public async Task ConfigBlock_ForASnapmakerU1_LeavesOutTheNotifier(string make, string model)
    {
        // The U1's stock Moonraker has no apprise, so a [notifier] section fails to load and
        // Moonraker shows "failed plugins: notifier". The U1 logs prints with printlog-bridge.
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user, make: make, model: model);

        var created = await CreateAsync(user, printer);

        Assert.Equal(
            ServerOnlyBlock($"https://printer-{printer}:{created.Secret}@api.3dprintlog.test/spoolman"),
            created.ConfigBlock);
    }

    [Theory]
    [InlineData("Snapmaker", "J1")]
    [InlineData("Snapmaker", "U10")]
    [InlineData("Bambu Lab", "U1")]
    [InlineData(null, "U1")]
    [InlineData("Snapmaker", null)]
    public async Task ConfigBlock_KeepsTheNotifier_ForPrintersThatAreNotAU1(string? make, string? model)
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user, make: make, model: model);

        var created = await CreateAsync(user, printer);

        Assert.Contains("[notifier 3d_print_log]\n", created.ConfigBlock);
    }

    [Fact]
    public async Task ConfigBlock_IgnoresAnotherUsersBridgeOnTheSamePrinterId()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);
        var stranger = await CreateUserAsync();
        await AddConnectionAsync(stranger, printer, Connection.MoonrakerKind, DateTime.UtcNow);

        var created = await CreateAsync(user, printer);

        Assert.Contains("[notifier 3d_print_log]\n", created.ConfigBlock);
    }

    [Fact]
    public async Task Create_StoresOnlyAHashOfTheSecret()
    {
        var user = await CreateUserAsync();
        var created = await CreateAsync(user, await CreatePrinterAsync(user));

        var row = await WithDbAsync(db => db.SpoolmanConnections.SingleAsync(c => c.Id == created.Id, Ct));

        Assert.Equal("SHA256", row.HashAlgorithm);
        Assert.NotEqual(created.Secret, row.SecretHash);
        Assert.DoesNotContain(created.Secret!, row.SecretHash);
        Assert.Equal(SpoolmanSecret.Hash(created.Secret!), row.SecretHash);
    }

    [Fact]
    public async Task TheSecret_IsOnlyEverReturnedOnce()
    {
        var user = await CreateUserAsync();
        var created = await CreateAsync(user, await CreatePrinterAsync(user));

        var list = await SendAsync(HttpMethod.Get, "/api/SpoolmanConnections", user);
        var body = await list.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(created.Id.ToString(), body);
        Assert.DoesNotContain(created.Secret!, body);
        Assert.DoesNotContain("\"secret\"", body);
        Assert.DoesNotContain("configBlock", body);
    }

    [Fact]
    public async Task Recreate_RotatesTheSecretAndRevokesTheOldConnection()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);

        var first = await CreateAsync(user, printer);
        var second = await CreateAsync(user, printer);

        Assert.NotEqual(first.Secret, second.Secret);
        var list = await ListAsync(user);
        Assert.Equal("revoked", Assert.Single(list, c => c.Id == first.Id).State);
        Assert.Equal("active", Assert.Single(list, c => c.Id == second.Id).State);
    }

    [Fact]
    public async Task Create_ForAnotherUsersPrinter_Is404()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var printer = await CreatePrinterAsync(owner);

        var response = await SendAsync(HttpMethod.Post, $"/api/Printers/{printer}/spoolman-connection", stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.SpoolmanConnections.CountAsync(c => c.PrinterId == printer, Ct)));
    }

    /// <summary>
    /// 401 rather than the 403 the issue names: InteractiveUserOnly pins the interactive scheme,
    /// so authorization re-authenticates with it and the API-key principal never reaches the
    /// policy — the same outcome <c>DevicesControllerTests.RegisterDevice_RejectsApiKeyPrincipal</c>
    /// pins. What matters is that no credential is minted.
    /// </summary>
    [Fact]
    public async Task AnApiKeyCaller_CannotMintAConnection()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);
        string apiKey;
        using (var scope = factory.Services.CreateScope())
        {
            apiKey = (await scope.ServiceProvider.GetRequiredService<IUserApiKeyService>()
                .GenerateNewApiKey(user.Id, "spoolman-test")).PublicKey!;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Printers/{printer}/spoolman-connection");
        request.Headers.Add("X-Api-Key", apiKey);
        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.SpoolmanConnections.CountAsync(c => c.UserId == user.Id, Ct)));
    }

    #endregion

    #region Tier limits

    [Fact]
    public async Task Free_ASecondPrinter_Is403NamingTheLimit()
    {
        var user = await CreateUserAsync();
        await CreateAsync(user, await CreatePrinterAsync(user));

        var response = await SendAsync(HttpMethod.Post, $"/api/Printers/{await CreatePrinterAsync(user)}/spoolman-connection", user);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("spoolman_connection_limit", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, problem.RootElement.GetProperty("limit").GetInt32());
        Assert.Contains("1", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Free_ReconnectingItsOnePrinter_IsAllowed()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);
        await CreateAsync(user, printer);

        var response = await SendAsync(HttpMethod.Post, $"/api/Printers/{printer}/spoolman-connection", user);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Free_AfterRevokingItsConnection_CanConnectAnotherPrinter()
    {
        var user = await CreateUserAsync();
        var first = await CreateAsync(user, await CreatePrinterAsync(user));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"/api/SpoolmanConnections/{first.Id}", user)).StatusCode);

        var response = await SendAsync(HttpMethod.Post, $"/api/Printers/{await CreatePrinterAsync(user)}/spoolman-connection", user);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Pro_IsUnlimited()
    {
        var user = await CreateUserAsync(pro: true);

        for (var i = 0; i < 4; i++)
        {
            await CreateAsync(user, await CreatePrinterAsync(user));
        }

        Assert.Equal(4, (await ListAsync(user)).Count(c => c.State == "active"));
    }

    [Fact]
    public async Task Downgrade_DisablesAllButTheOldest_AndResubscribing_ReenablesThem()
    {
        var user = await CreateUserAsync(pro: true);
        var oldest = await CreateAsync(user, await CreatePrinterAsync(user));
        var second = await CreateAsync(user, await CreatePrinterAsync(user));
        var third = await CreateAsync(user, await CreatePrinterAsync(user));

        await PostStripeEventAsync("customer.subscription.deleted", user, status: "canceled");

        var paused = await ListAsync(user);
        Assert.Equal("active", Assert.Single(paused, c => c.Id == oldest.Id).State);
        foreach (var id in new[] { second.Id, third.Id })
        {
            var connection = Assert.Single(paused, c => c.Id == id);
            Assert.Equal("disabled", connection.State);
            Assert.Equal(SpoolmanConnection.TierDisabledReason, connection.DisabledReason);
        }
        // Paused, not deleted: the rows are all still there.
        Assert.Equal(3, await WithDbAsync(db => db.SpoolmanConnections.CountAsync(c => c.UserId == user.Id, Ct)));

        await PostStripeEventAsync("customer.subscription.updated", user, status: "active");

        Assert.All(await ListAsync(user), c => Assert.Equal("active", c.State));
    }

    [Fact]
    public async Task Downgrade_LeavesARevokedConnectionRevoked()
    {
        var user = await CreateUserAsync(pro: true);
        var revoked = await CreateAsync(user, await CreatePrinterAsync(user));
        await SendAsync(HttpMethod.Delete, $"/api/SpoolmanConnections/{revoked.Id}", user);
        var survivor = await CreateAsync(user, await CreatePrinterAsync(user));

        await PostStripeEventAsync("customer.subscription.deleted", user, status: "canceled");
        await PostStripeEventAsync("customer.subscription.updated", user, status: "active");

        var list = await ListAsync(user);
        Assert.Equal("revoked", Assert.Single(list, c => c.Id == revoked.Id).State);
        Assert.Equal("active", Assert.Single(list, c => c.Id == survivor.Id).State);
    }

    #endregion

    #region Revoking

    [Fact]
    public async Task Delete_RevokesTheConnection()
    {
        var user = await CreateUserAsync();
        var created = await CreateAsync(user, await CreatePrinterAsync(user));

        var response = await SendAsync(HttpMethod.Delete, $"/api/SpoolmanConnections/{created.Id}", user);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("revoked", Assert.Single(await ListAsync(user)).State);
    }

    [Fact]
    public async Task Delete_AnotherUsersConnection_Is404()
    {
        var owner = await CreateUserAsync();
        var stranger = await CreateUserAsync();
        var created = await CreateAsync(owner, await CreatePrinterAsync(owner));

        var response = await SendAsync(HttpMethod.Delete, $"/api/SpoolmanConnections/{created.Id}", stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("active", Assert.Single(await ListAsync(owner)).State);
    }

    [Fact]
    public async Task DeletingThePrinter_RevokesItsConnection()
    {
        var user = await CreateUserAsync();
        var printer = await CreatePrinterAsync(user);
        var created = await CreateAsync(user, printer);

        var response = await SendAsync(HttpMethod.Delete, $"/api/Printers/{printer}", user);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");

        var row = await WithDbAsync(db => db.SpoolmanConnections.SingleAsync(c => c.Id == created.Id, Ct));
        Assert.NotNull(row.RevokedAt);
        Assert.Null(row.PrinterId);
    }

    [Fact]
    public async Task AccountDeletion_RemovesConnections()
    {
        var user = await CreateUserAsync();
        await CreateAsync(user, await CreatePrinterAsync(user));
        await WithDbAsync(async db =>
        {
            db.SpoolmanNetworkSightings.Add(new SpoolmanNetworkSighting { UserId = user.Id, NetworkHash = new string('a', 64), FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
            db.SpoolmanFairUseAlerts.Add(new SpoolmanFairUseAlert { UserId = user.Id, Signal = SpoolmanFairUseAlert.ConnectionCountSignal, LastSentAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var tracked = await db.Users.SingleAsync(u => u.Id == user.Id, Ct);
            await scope.ServiceProvider.GetRequiredService<IUserDeletionService>().DeleteAllDataForUser(tracked);
        }

        Assert.Equal(0, await WithDbAsync(db => db.SpoolmanConnections.CountAsync(c => c.UserId == user.Id, Ct)));
        Assert.Equal(0, await WithDbAsync(db => db.SpoolmanNetworkSightings.CountAsync(c => c.UserId == user.Id, Ct)));
        Assert.Equal(0, await WithDbAsync(db => db.SpoolmanFairUseAlerts.CountAsync(c => c.UserId == user.Id, Ct)));
    }

    #endregion

    #region Fair use

    [Fact]
    public async Task FairUseEmail_FiresPastTwentyConnections_AndAgainOnlyAfterTheCooldown()
    {
        var user = await CreateUserAsync(pro: true);
        var marker = $"User id: {user.Id}<br>";

        for (var i = 0; i < 20; i++)
        {
            await CreateAsync(user, await CreatePrinterAsync(user));
        }
        Assert.Empty(factory.EmailSender.Matching(marker));

        await CreateAsync(user, await CreatePrinterAsync(user)); // the 21st
        var email = Assert.Single(factory.EmailSender.Matching(marker));
        Assert.Equal(AdminAddress, email.To);
        Assert.Contains("21", email.Body);

        await CreateAsync(user, await CreatePrinterAsync(user)); // the 22nd, inside the week
        Assert.Single(factory.EmailSender.Matching(marker));

        // A week on.
        await WithDbAsync(db => db.SpoolmanFairUseAlerts
            .Where(a => a.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSentAt, DateTime.UtcNow.AddDays(-8)), Ct));

        await CreateAsync(user, await CreatePrinterAsync(user)); // the 23rd
        Assert.Equal(2, factory.EmailSender.Matching(marker).Count);
    }

    [Fact]
    public async Task FairUseEmail_CarriesNoSecret()
    {
        var user = await CreateUserAsync(pro: true);
        var secrets = new List<string>();
        for (var i = 0; i < 21; i++)
        {
            secrets.Add((await CreateAsync(user, await CreatePrinterAsync(user))).Secret!);
        }

        var email = Assert.Single(factory.EmailSender.Matching($"User id: {user.Id}<br>"));
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, email.Body));
    }

    [Fact]
    public async Task NetworkEmail_FiresPastFiveNetworksInAWeek_CountingEachPrefixOnce()
    {
        var user = await CreateUserAsync();
        var marker = $"User id: {user.Id}<br>";
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>();

        // Many hosts in one /24, and many in one /48, are one network each.
        foreach (var host in new[] { "203.0.113.4", "203.0.113.200", "2001:db8:1:aaaa::1", "2001:db8:1:bbbb::2" })
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse(host), Ct);
        }
        foreach (var host in new[] { "198.51.100.1", "192.0.2.1", "10.1.2.3" })
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse(host), Ct);
        }
        Assert.Empty(factory.EmailSender.Matching(marker)); // five networks

        await service.RecordNetworkAsync(user.Id, IPAddress.Parse("100.64.9.9"), Ct); // the sixth
        Assert.Single(factory.EmailSender.Matching(marker));

        await service.RecordNetworkAsync(user.Id, IPAddress.Parse("172.16.5.5"), Ct); // inside the cooldown
        Assert.Single(factory.EmailSender.Matching(marker));
    }

    [Fact]
    public async Task NetworkEmail_IgnoresNetworksNotSeenThisWeek()
    {
        var user = await CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>();
        for (var i = 1; i <= 5; i++)
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse($"198.18.{i}.1"), Ct);
        }
        await WithDbAsync(db => db.SpoolmanNetworkSightings
            .Where(s => s.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, DateTime.UtcNow.AddDays(-8)), Ct));

        await service.RecordNetworkAsync(user.Id, IPAddress.Parse("198.19.0.1"), Ct);

        Assert.Empty(factory.EmailSender.Matching($"User id: {user.Id}<br>"));
    }

    [Fact]
    public async Task NetworkEmail_AFailedSend_DeletesTheClaimItInserted_SoTheNextTriggerSends()
    {
        var user = await CreateUserAsync();
        var marker = $"User id: {user.Id}<br>";
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>();
        for (var i = 1; i <= 5; i++)
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse($"198.18.{i}.1"), Ct);
        }

        factory.EmailSender.ThrowOnSend = true;
        try
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse("198.18.6.1"), Ct); // the sixth
        }
        finally
        {
            factory.EmailSender.ThrowOnSend = false;
        }
        Assert.Empty(factory.EmailSender.Matching(marker));
        Assert.Equal(0, await WithDbAsync(db => db.SpoolmanFairUseAlerts.CountAsync(a => a.UserId == user.Id, Ct)));

        await service.RecordNetworkAsync(user.Id, IPAddress.Parse("198.18.7.1"), Ct);

        Assert.Single(factory.EmailSender.Matching(marker));
    }

    [Fact]
    public async Task NetworkEmail_AFailedSend_RestoresThePreviousCooldown_SoTheNextTriggerSends()
    {
        var user = await CreateUserAsync();
        var marker = $"User id: {user.Id}<br>";
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>();
        for (var i = 1; i <= 6; i++)
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse($"198.18.{i}.1"), Ct);
        }
        Assert.Single(factory.EmailSender.Matching(marker));

        // A week on, the cool-down has lapsed.
        var lapsed = DateTime.UtcNow.AddDays(-8);
        await WithDbAsync(db => db.SpoolmanFairUseAlerts
            .Where(a => a.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSentAt, lapsed), Ct));

        factory.EmailSender.ThrowOnSend = true;
        try
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse("198.18.7.1"), Ct);
        }
        finally
        {
            factory.EmailSender.ThrowOnSend = false;
        }
        Assert.Single(factory.EmailSender.Matching(marker));
        var restored = await WithDbAsync(db => db.SpoolmanFairUseAlerts.SingleAsync(a => a.UserId == user.Id, Ct));
        Assert.Equal(lapsed, restored.LastSentAt, TimeSpan.FromMilliseconds(1));

        await service.RecordNetworkAsync(user.Id, IPAddress.Parse("198.18.8.1"), Ct);

        Assert.Equal(2, factory.EmailSender.Matching(marker).Count);
    }

    [Fact]
    public async Task Networks_StopBeingStoredAtTheWeeklyCap_WithoutAffectingTheAlert()
    {
        var user = await CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>();

        for (var i = 1; i <= SpoolmanNetworkSighting.MaxPerUserPerWeek + 5; i++)
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse($"198.18.{i}.1"), Ct);
        }

        Assert.Equal(SpoolmanNetworkSighting.MaxPerUserPerWeek,
            await WithDbAsync(db => db.SpoolmanNetworkSightings.CountAsync(s => s.UserId == user.Id, Ct)));
        Assert.Single(factory.EmailSender.Matching($"User id: {user.Id}<br>"));
    }

    [Fact]
    public async Task Networks_ACapAtOrBelowAConfiguredThreshold_IsRaised_SoTheAlertStillFires()
    {
        var user = await CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var configured = scope.ServiceProvider.GetRequiredService<IOptions<SpoolmanOptions>>().Value;
        var threshold = SpoolmanNetworkSighting.MaxPerUserPerWeek;
        var service = ActivatorUtilities.CreateInstance<SpoolmanConnectionService>(scope.ServiceProvider,
            Options.Create(new SpoolmanOptions
            {
                ServerUrl = configured.ServerUrl,
                AdminEmailAddress = configured.AdminEmailAddress,
                NetworkHashKey = configured.NetworkHashKey,
                FairUseNetworkThreshold = threshold,
            }));

        for (var i = 1; i <= threshold + 1; i++)
        {
            await service.RecordNetworkAsync(user.Id, IPAddress.Parse($"198.18.{i}.1"), Ct);
        }

        Assert.Equal(threshold + 1, await WithDbAsync(db => db.SpoolmanNetworkSightings.CountAsync(s => s.UserId == user.Id, Ct)));
        Assert.Single(factory.EmailSender.Matching($"User id: {user.Id}<br>"));
    }

    [Fact]
    public async Task Networks_AreStoredAsKeyedHashesOfThePrefix_NeverRawAddresses()
    {
        var user = await CreateUserAsync();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>()
                .RecordNetworkAsync(user.Id, IPAddress.Parse("203.0.113.77"), Ct);
        }

        var stored = await WithDbAsync(db => db.SpoolmanNetworkSightings.Where(s => s.UserId == user.Id).Select(s => s.NetworkHash).SingleAsync(Ct));

        Assert.Matches(new Regex("^[0-9a-f]{64}$"), stored);
        Assert.DoesNotContain("203.0.113", stored);
        // Not a bare hash of the prefix, which anyone could enumerate in 2^24 guesses.
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("203.0.113.0/24"))), stored);
    }

    [Theory]
    [InlineData("203.0.113.77", "203.0.113.1")]
    [InlineData("2001:db8:1:ffff::1", "2001:db8:1::9")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.6")]
    public void NetworkPrefix_GroupsHostsOfOneNetwork(string a, string b)
    {
        Assert.Equal(SpoolmanNetwork.Prefix(IPAddress.Parse(a)), SpoolmanNetwork.Prefix(IPAddress.Parse(b)));
    }

    [Theory]
    [InlineData("203.0.113.77", "203.0.114.77")]
    [InlineData("2001:db8:1::1", "2001:db8:2::1")]
    public void NetworkPrefix_SeparatesNetworks(string a, string b)
    {
        Assert.NotEqual(SpoolmanNetwork.Prefix(IPAddress.Parse(a)), SpoolmanNetwork.Prefix(IPAddress.Parse(b)));
    }

    #endregion

    #region Helpers

    private async Task<SpoolmanConnectionCreatedDto> CreateAsync(User user, long printerId)
    {
        var response = await SendAsync(HttpMethod.Post, $"/api/Printers/{printerId}/spoolman-connection", user);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<SpoolmanConnectionCreatedDto>(Ct))!;
    }

    /// <summary>Registers a connector the way a heartbeat would leave it, and returns its instance id.</summary>
    private async Task<string> AddConnectionAsync(User user, long? printerId, string kind, DateTime lastSeenAt)
    {
        return await WithDbAsync(async db =>
        {
            var connection = new Connection
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Kind = kind,
                InstanceId = Guid.NewGuid().ToString("N"),
                DisplayName = "Bridge",
                PrinterId = printerId,
                CreatedDate = lastSeenAt,
                LastSeenAt = lastSeenAt,
            };
            db.Connections.Add(connection);
            await db.SaveChangesAsync(Ct);
            return connection.InstanceId;
        });
    }

    private async Task<List<SpoolmanConnectionDto>> ListAsync(User user)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/SpoolmanConnections", user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<SpoolmanConnectionDto>>(Ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, User user)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return await _client.SendAsync(request, Ct);
    }

    private async Task<T> WithDbAsync<T>(Func<PrintLogContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
    }

    private async Task<User> CreateUserAsync(bool pro = false)
    {
        return await WithDbAsync(async db =>
        {
            var user = new User
            {
                OAuthUserId = $"auth0|spoolman-{Guid.NewGuid():N}",
                DisplayName = "Spoolman",
                ViewStatus = User.ProfileViewStatus.Private,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(Ct);

            if (pro)
            {
                db.Subscriptions.Add(new Subscription
                {
                    UserId = user.Id,
                    Status = SubscriptionStatus.Active,
                    Plan = SubscriptionPlan.ProMonthly,
                    StripeCustomerId = $"cus_{user.Id}",
                    StripeSubscriptionId = $"sub_{user.Id}",
                    CreatedById = user.Id,
                    UpdatedById = user.Id,
                });
                await db.SaveChangesAsync(Ct);
            }

            return user;
        });
    }

    private async Task<long> CreatePrinterAsync(User user, string? name = null, string? make = null, string? model = null)
    {
        return await WithDbAsync(async db =>
        {
            var printer = new Printer { Name = name ?? $"Printer {Guid.NewGuid():N}"[..20], UserId = user.Id, IsActive = true, CategoryNickname = "FDM", Make = make, Model = model };
            db.Printers.Add(printer);
            await db.SaveChangesAsync(Ct);
            return printer.Id;
        });
    }

    /// <summary>
    /// Posts a Stripe subscription event signed the way Stripe signs them, through the real
    /// webhook endpoint, so the downgrade is applied by the path production takes.
    /// </summary>
    private async Task PostStripeEventAsync(string type, User user, string status)
    {
        var json = JsonSerializer.Serialize(new
        {
            id = $"evt_{Guid.NewGuid():N}",
            @object = "event",
            api_version = Stripe.StripeConfiguration.ApiVersion,
            type,
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            livemode = false,
            pending_webhooks = 1,
            request = new { id = (string?)null, idempotency_key = (string?)null },
            data = new
            {
                @object = new
                {
                    id = $"sub_{user.Id}",
                    @object = "subscription",
                    customer = $"cus_{user.Id}",
                    status,
                    cancel_at_period_end = false,
                    items = new { @object = "list", data = Array.Empty<object>() },
                },
            },
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{timestamp}.{json}")));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Subscription/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    #endregion
}
