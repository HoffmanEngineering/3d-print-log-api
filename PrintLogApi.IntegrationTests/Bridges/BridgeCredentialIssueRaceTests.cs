using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Bridges;
using PrintLogApi.Services.Bridges;
using Xunit;
using static PrintLogApi.IntegrationTests.Bridges.BridgeTestKit;

namespace PrintLogApi.IntegrationTests.Bridges;

/// <summary>
/// A check-in that read the entries before the owner sealed a new value must not write its issues
/// over the ones that seal cleared (story 14a review): the issue write is conditional.
/// </summary>
public class BridgeCredentialIssueRaceTests(BridgeCredentialIssueRaceTests.Factory factory) : IClassFixture<BridgeCredentialIssueRaceTests.Factory>
{
    public class Factory : CustomWebApplicationFactory
    {
        public IssueWriteInterceptor Interceptor { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.ConfigureDbContext<PrintLogContext>(options => options.AddInterceptors(Interceptor)));
        }
    }

    /// <summary>Runs <see cref="Before"/> once, just before the next save that writes credential issues.</summary>
    public sealed class IssueWriteInterceptor : SaveChangesInterceptor
    {
        public Func<Task>? Before { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Before is { } before && eventData.Context!.ChangeTracker.Entries<BridgePrinter>()
                    .Any(e => e.State == EntityState.Modified && e.Property(p => p.CredentialIssues).IsModified))
            {
                Before = null;
                await before();
            }

            return result;
        }
    }

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ASealDuringACheckIn_KeepsItsClearedIssue()
    {
        var bridge = await PairBridgeAsync(_client, "race");
        using var key = new BridgeSealedCredentialTests.BridgeKey();
        await CheckInOkAsync(_client, bridge.Token, new { publicKey = key.PublicKey, discovered = new[] { Bambu(serial: "RACE" + Guid.NewGuid().ToString("N")[..10]) } });
        var entryId = (await ConnectAsync(_client, bridge, Assert.Single(await DiscoveredAsync(_client, bridge)).GetProperty("id").GetInt64())).GetProperty("id").GetInt64();
        var version = (await CheckInOkAsync(_client, bridge.Token, new { })).GetProperty("desired").GetProperty("version").GetString();
        await CheckInOkAsync(_client, bridge.Token, new
        {
            credentialIssuesVersion = version,
            credentialIssues = new[] { new { entryId, field = "access_code", code = "missing" } },
        });
        var userId = await UserIdAsync(factory, bridge.Subject);

        // The owner seals a value while the next check-in is about to write its issues.
        factory.Interceptor.Before = async () =>
        {
            using var scope = factory.Services.CreateScope();
            var bridges = scope.ServiceProvider.GetRequiredService<IBridgeService>();
            var sealedValue = CredentialSealer.Seal(key.PublicKey, CredentialSealer.AssociatedData(bridge.GrantId, entryId, "access_code"), "12345678");
            Assert.NotNull(await bridges.SetSecretAsync(userId, bridge.GrantId, entryId, "access_code",
                new SetBridgePrinterSecretDto { Sealed = sealedValue, KeyFingerprint = key.Fingerprint }, TestContext.Current.CancellationToken));
        };
        var checkIn = await _client.SendAsync(CheckIn(bridge.Token, new
        {
            credentialIssuesVersion = version,
            credentialIssues = new[] { new { entryId, field = "access_code", code = "undecryptable" } },
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        Assert.Null(factory.Interceptor.Before);
        var owner = (await GetAsUserAsync(_client, bridge.Subject, $"/api/bridges/{bridge.GrantId}/printers")).EnumerateArray().Single();
        Assert.Empty(owner.GetProperty("credentialIssues").EnumerateArray());
        Assert.Single(owner.GetProperty("secrets").EnumerateArray());
    }
}
