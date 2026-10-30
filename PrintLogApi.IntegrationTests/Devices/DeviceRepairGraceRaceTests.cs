using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Services.DeviceGrants;
using Xunit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// The re-pair grace period and the idempotent retry (api#287, item 3) against the races the
/// issue names: the grace running out, a revoke inside it, two re-pairs at once, and a save that
/// committed but whose acknowledgment was lost (what an EF execution-strategy retry then sees).
/// </summary>
public class DeviceRepairGraceRaceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly IOptions<DevicePairingOptions> Options = Microsoft.Extensions.Options.Options.Create(new DevicePairingOptions { MaxGrantsPerUser = 25 });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PairingTokenSpend Spend(string label) =>
        new(DeviceTokenFormat.Hash("jti:" + label + Guid.NewGuid()), Start.UtcDateTime.AddMinutes(15));

    private sealed class Interceptor(Func<DbContext, Task>? beforeSave = null, bool loseTheAcknowledgment = false) : SaveChangesInterceptor
    {
        private bool _armed = true;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (beforeSave is not null && _armed)
            {
                _armed = false;
                await beforeSave(eventData.Context!);
            }

            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (loseTheAcknowledgment && _armed)
            {
                _armed = false;
                throw new DbUpdateException("The connection dropped after the commit.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class World : IDisposable
    {
        private readonly IServiceScope _scope;

        public World(CustomWebApplicationFactory factory)
        {
            _scope = factory.Services.CreateScope();
            Shared = _scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            Cache = _scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        }

        public PrintLogContext Shared { get; }

        public IMemoryCache Cache { get; }

        public SettableTimeProvider Clock { get; } = new(Start);

        public long UserId => IntegrationTestSeeder.TestUserId;

        public PrintLogContext Context(SaveChangesInterceptor? interceptor = null)
        {
            var builder = new DbContextOptionsBuilder<PrintLogContext>()
                .UseSqlite(Shared.Database.GetDbConnection())
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            return new PrintLogContext(builder.Options);
        }

        public DeviceGrantService Service(PrintLogContext? context = null) => new(context ?? Context(), Cache, Options, Clock);

        public Task<PairedDevice> PairAsync(Guid deviceId, PairingTokenSpend spend, PrintLogContext? context = null) =>
            Service(context).PairAsync(UserId, deviceId, "bridge", [DeviceScopes.PrintsRead], "client", spend, null, Ct);

        public Task<DeviceGrantIdentity?> AuthenticateAsync(string token) => Service().AuthenticateAsync(token, Ct);

        public void Dispose() => _scope.Dispose();
    }

    [Fact]
    public async Task TheOldToken_StopsAfterTheGracePeriod_EvenIfTheNewOneIsNeverUsed()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        var old = (await w.PairAsync(deviceId, Spend("first"))).Token;
        await w.PairAsync(deviceId, Spend("repair"));

        w.Clock.SetUtcNow(Start + DeviceGrantService.RepairGrace - TimeSpan.FromSeconds(1));
        var inGrace = await w.AuthenticateAsync(old);
        Assert.NotNull(inGrace);
        Assert.True(inGrace.Superseded);

        w.Clock.SetUtcNow(Start + DeviceGrantService.RepairGrace);
        Assert.Null(await w.AuthenticateAsync(old));
    }

    [Fact]
    public async Task ARePairDuringTheGrace_KeepsTheTokenTheDeviceProbablyHolds_AndItsDeadline()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        var original = (await w.PairAsync(deviceId, Spend("first"))).Token;
        var neverUsed = (await w.PairAsync(deviceId, Spend("second"))).Token;
        w.Clock.SetUtcNow(Start + TimeSpan.FromMinutes(5));
        var latest = (await w.PairAsync(deviceId, Spend("third"))).Token;

        Assert.NotNull(await w.AuthenticateAsync(original));
        Assert.Null(await w.AuthenticateAsync(neverUsed));

        // The deadline is the first rotation's: re-pairing again cannot stretch it.
        w.Clock.SetUtcNow(Start + DeviceGrantService.RepairGrace);
        Assert.Null(await w.AuthenticateAsync(original));
        Assert.NotNull(await w.AuthenticateAsync(latest));
    }

    [Fact]
    public async Task ARevoke_BetweenARePairsReadAndItsSave_IsDeviceGrantRevoked_AndLeavesNoTokenWorking()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        var original = (await w.PairAsync(deviceId, Spend("first"))).Token;
        var first = await w.PairAsync(deviceId, Spend("repair"));

        var revoking = new Interceptor(beforeSave: c => c.Database.ExecuteSqlRawAsync("DELETE FROM DeviceGrants WHERE Id = {0}", [first.Grant.Id], Ct));
        await using var racing = w.Context(revoking);

        await Assert.ThrowsAsync<DeviceGrantRevokedException>(() => w.PairAsync(deviceId, Spend("repair-again"), racing));

        Assert.Null(await w.AuthenticateAsync(original));
        Assert.Null(await w.AuthenticateAsync(first.Token));
        await using var check = w.Context();
        Assert.False(await check.DeviceGrants.AnyAsync(g => g.DeviceId == deviceId, Ct));
    }

    [Fact]
    public async Task TwoRePairsAtOnce_BothAnswer_AndTheDeviceKeepsAWorkingToken()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        var original = (await w.PairAsync(deviceId, Spend("first"))).Token;
        PairedDevice? inner = null;

        // The second re-pair runs to completion while the first is between its read and its save.
        var interleaved = new Interceptor(beforeSave: async _ => inner = await w.PairAsync(deviceId, Spend("inner")));
        await using var racing = w.Context(interleaved);
        var outer = await w.PairAsync(deviceId, Spend("outer"), racing);

        Assert.NotNull(inner);
        Assert.Equal(inner.Grant.Id, outer.Grant.Id);

        // The last save wins the rotation; the token both read as current stays in its grace.
        await using var check = w.Context();
        var row = await check.DeviceGrants.AsNoTracking().SingleAsync(g => g.DeviceId == deviceId, Ct);
        Assert.True(row.TokenHash == DeviceTokenFormat.Hash(outer.Token),
            $"current is {(row.TokenHash == DeviceTokenFormat.Hash(inner.Token) ? "inner" : row.TokenHash == DeviceTokenFormat.Hash(original) ? "original" : "other")}, previous is {(row.PreviousTokenHash == DeviceTokenFormat.Hash(original) ? "original" : row.PreviousTokenHash ?? "null")}");
        Assert.NotNull(await w.AuthenticateAsync(original));
        Assert.Null(await w.AuthenticateAsync(inner.Token));
        Assert.NotNull(await w.AuthenticateAsync(outer.Token));
    }

    [Fact]
    public async Task ARePair_RacingTheNewTokensFirstUse_LeavesThatTokenWorking()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        await w.PairAsync(deviceId, Spend("first"));
        var second = (await w.PairAsync(deviceId, Spend("second"))).Token;

        // The device uses its new token (ending the first token's grace) while a third pairing is
        // between its read, which still saw that grace, and its save.
        var firstUse = new Interceptor(beforeSave: async _ => Assert.NotNull(await w.AuthenticateAsync(second)));
        await using var racing = w.Context(firstUse);
        var third = await w.PairAsync(deviceId, Spend("third"), racing);

        // The token the device just proved it holds must stay usable through the new grace.
        var identity = await w.AuthenticateAsync(second);
        Assert.NotNull(identity);
        Assert.True(identity.Superseded);
        Assert.NotNull(await w.AuthenticateAsync(third.Token));
    }

    [Fact]
    public async Task TwoExchangesOfOneApproval_AtOnce_OnlyOneSucceeds()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        await w.PairAsync(deviceId, Spend("first"));
        var shared = Spend("shared");
        PairedDevice? inner = null;

        var interleaved = new Interceptor(beforeSave: async _ => inner = await w.PairAsync(deviceId, shared));
        await using var racing = w.Context(interleaved);

        await Assert.ThrowsAsync<PairingTokenUsedException>(() => w.PairAsync(deviceId, shared, racing));
        Assert.NotNull(inner);
        Assert.NotNull(await w.AuthenticateAsync(inner.Token));
    }

    [Fact]
    public async Task TwoFirstExchangesOfOneApproval_AtOnce_OnlyOneSucceeds()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        var shared = Spend("shared-first");
        PairedDevice? inner = null;

        var interleaved = new Interceptor(beforeSave: async _ => inner = await w.PairAsync(deviceId, shared));
        await using var racing = w.Context(interleaved);

        await Assert.ThrowsAsync<PairingTokenUsedException>(() => w.PairAsync(deviceId, shared, racing));
        Assert.NotNull(inner);
        await using var check = w.Context();
        Assert.Equal(1, await check.DeviceGrants.CountAsync(g => g.DeviceId == deviceId, Ct));
    }

    [Fact]
    public async Task ASaveThatCommittedButWasNotAcknowledged_IsTheSuccessItWas_NotPairingTokenUsed()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();
        var original = (await w.PairAsync(deviceId, Spend("first"))).Token;

        await using var lossy = w.Context(new Interceptor(loseTheAcknowledgment: true));
        var paired = await w.PairAsync(deviceId, Spend("lost-ack"), lossy);

        // The replaced token is in its grace until the new one is used.
        Assert.NotNull(await w.AuthenticateAsync(original));
        var identity = await w.AuthenticateAsync(paired.Token);
        Assert.NotNull(identity);
        Assert.False(identity.Superseded);
    }

    [Fact]
    public async Task AFirstPairing_ThatCommittedButWasNotAcknowledged_IsCreated()
    {
        using var w = new World(factory);
        var deviceId = Guid.NewGuid();

        await using var lossy = w.Context(new Interceptor(loseTheAcknowledgment: true));
        var paired = await w.PairAsync(deviceId, Spend("lost-ack-first"), lossy);

        Assert.True(paired.Created);
        Assert.NotNull(await w.AuthenticateAsync(paired.Token));
        await using var check = w.Context();
        Assert.Equal(1, await check.DeviceGrants.CountAsync(g => g.DeviceId == deviceId, Ct));
    }
}
