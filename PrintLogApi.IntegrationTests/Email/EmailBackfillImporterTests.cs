using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Email.Backfill;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailBackfillImporterTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly CustomWebApplicationFactory _factory;

    public EmailBackfillImporterTests(CustomWebApplicationFactory factory) => _factory = factory;

    // Two lines of Auth0's users-export NDJSON, including fields the reader must ignore.
    private const string Ndjson =
        "{\"user_id\":\"auth0|one\",\"email\":\"One@Example.com\",\"email_verified\":true,\"created_at\":\"2024-03-01T10:00:00.000Z\",\"name\":\"x\"}\n" +
        "{\"user_id\":\"google-oauth2|two\",\"email\":\"two@example.com\",\"email_verified\":false}\n";

    private static async Task<List<Auth0ExportUser>> ReadAllAsync(Stream stream)
    {
        var users = new List<Auth0ExportUser>();
        await foreach (var u in Auth0ExportReader.ReadAsync(stream, TestContext.Current.CancellationToken))
        {
            users.Add(u);
        }
        return users;
    }

    private static async IAsyncEnumerable<Auth0ExportUser> AsAsync(params Auth0ExportUser[] users)
    {
        foreach (var u in users)
        {
            yield return u;
        }
        await Task.CompletedTask;
    }

    private static EmailBackfillImporter Importer(PrintLogContext db) => new(db, new SettableTimeProvider(Now));

    private static async Task<User> SeedAsync(PrintLogContext db, DateTimeOffset? createdDate = null)
    {
        var user = new User
        {
            OAuthUserId = $"auth0|backfill-{Guid.NewGuid():N}",
            ViewStatus = User.ProfileViewStatus.Public,
            CreatedDate = createdDate,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private static async Task<User> ReloadAsync(PrintLogContext db, long id)
        => await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reader_ParsesPlainAndGzippedNdjson()
    {
        var bytes = Encoding.UTF8.GetBytes(Ndjson);
        var gzipped = new MemoryStream();
        await using (var gz = new GZipStream(gzipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gz.WriteAsync(bytes, TestContext.Current.CancellationToken);
        }
        gzipped.Position = 0;

        var plain = await ReadAllAsync(new MemoryStream(bytes));
        var unzipped = await ReadAllAsync(gzipped);

        Assert.Equal(
            [
                new Auth0ExportUser("auth0|one", "One@Example.com", true, new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero)),
                new Auth0ExportUser("google-oauth2|two", "two@example.com", false, null),
            ],
            plain);
        Assert.Equal(plain, unzipped);
    }

    [Fact]
    public async Task Reader_SkipsBlankLines()
        => Assert.Single(await ReadAllAsync(new MemoryStream(Encoding.UTF8.GetBytes("\n{\"user_id\":\"a\"}\n\n"))));

    [Fact]
    public async Task Import_MatchesOnOAuthUserIdAndSetsFields()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await SeedAsync(db);
        var created = new DateTimeOffset(2024, 3, 1, 10, 0, 0, TimeSpan.Zero);

        var result = await Importer(db).ImportAsync(
            AsAsync(new Auth0ExportUser(user.OAuthUserId!, "  Maker@Example.com ", true, created)),
            dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(new EmailBackfillResult(Read: 1, Matched: 1, Updated: 1, Unmatched: 0, Verified: 1, Unverified: 0), result);
        var stored = await ReloadAsync(db, user.Id);
        Assert.Equal("Maker@Example.com", stored.Email);
        Assert.True(stored.EmailVerified);
        Assert.Equal(created, stored.CreatedDate);
        Assert.Equal(Now, stored.EmailUpdatedAt);
    }

    [Fact]
    public async Task Import_DoesNotOverwriteExistingCreatedDate()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var original = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var user = await SeedAsync(db, createdDate: original);

        await Importer(db).ImportAsync(
            AsAsync(new Auth0ExportUser(user.OAuthUserId!, "a@example.com", true, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))),
            dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(original, (await ReloadAsync(db, user.Id)).CreatedDate);
    }

    [Fact]
    public async Task Import_IsIdempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await SeedAsync(db);
        var row = new Auth0ExportUser(user.OAuthUserId!, "a@example.com", false, null);

        await Importer(db).ImportAsync(AsAsync(row), dryRun: false, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var second = await Importer(db).ImportAsync(AsAsync(row), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(new EmailBackfillResult(Read: 1, Matched: 1, Updated: 0, Unmatched: 0, Verified: 0, Unverified: 1), second);
    }

    [Fact]
    public async Task Import_DryRunWritesNothing()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await SeedAsync(db);

        var result = await Importer(db).ImportAsync(
            AsAsync(new Auth0ExportUser(user.OAuthUserId!, "a@example.com", true, Now.AddYears(-1))),
            dryRun: true, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Updated);
        var stored = await ReloadAsync(db, user.Id);
        Assert.Null(stored.Email);
        Assert.Null(stored.CreatedDate);
    }

    [Fact]
    public async Task Import_CountsUnmatched()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var result = await Importer(db).ImportAsync(
            AsAsync(new Auth0ExportUser($"auth0|nobody-{Guid.NewGuid():N}", "x@example.com", true, null)),
            dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(new EmailBackfillResult(Read: 1, Matched: 0, Updated: 0, Unmatched: 1, Verified: 0, Unverified: 0), result);
    }

    // An Auth0 account with no address (some social connections) must not blank a stored one.
    [Fact]
    public async Task Import_MissingEmail_LeavesStoredAddress()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db, email: "kept@example.com");

        await Importer(db).ImportAsync(
            AsAsync(new Auth0ExportUser(user.OAuthUserId!, null, false, null)),
            dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal("kept@example.com", (await ReloadAsync(db, user.Id)).Email);
    }

    // More than one batch, so the batching loop's boundary is exercised.
    [Fact]
    public async Task Import_SpansBatches()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var seeded = new List<User>();
        for (var i = 0; i < EmailBackfillImporter.BatchSize + 3; i++)
        {
            seeded.Add(new User { OAuthUserId = $"auth0|batch-{Guid.NewGuid():N}", ViewStatus = User.ProfileViewStatus.Public });
        }
        db.Users.AddRange(seeded);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Importer(db).ImportAsync(
            AsAsync([.. seeded.Select(u => new Auth0ExportUser(u.OAuthUserId!, $"{u.Id}@example.com", true, null))]),
            dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(seeded.Count, result.Updated);
    }
}
