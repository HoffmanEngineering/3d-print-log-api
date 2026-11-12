using System.Net;
using System.Text.Json;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Controllers.PublicReadSeed;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The per-user totals (<c>/api/Users/{id}/print-count</c>, <c>total-print-time</c>,
/// <c>total-filament-usage</c>) follow the profile's visibility, as <c>GET /api/Users/{id}</c>
/// does: a Private profile's totals are the owner's alone, and anyone else gets the same 403.
/// </summary>
public class UsersPrintsVisibilityTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly TheoryData<string> Endpoints = new() { "print-count", "total-print-time", "total-filament-usage" };

    private static string Url(long userId, string endpoint) =>
        $"/api/Users/{userId}/{endpoint}" +
        $"?fromDate={Uri.EscapeDataString(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("o"))}" +
        $"&toDate={Uri.EscapeDataString(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero).ToString("o"))}";

    private static string Expected(string endpoint) => endpoint switch
    {
        "print-count" => "1",
        "total-print-time" => "7777",
        _ => "15000", // both rows of the seeded print: 12 g on the spool, 3 g with none
    };

    private async Task<Owner> OwnerWithOnePrintAsync(User.ProfileViewStatus profile)
    {
        var owner = await OwnerAsync(factory, profile);
        await PrintAsync(factory, owner, PrintViewStatus.Public, seconds: 7777,
            start: new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero));
        return owner;
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task PrivateProfile_TotalsAreForbidden_ToAnonymousAndToOtherUsers(string endpoint)
    {
        var owner = await OwnerWithOnePrintAsync(User.ProfileViewStatus.Private);
        var other = await OtherUserAsync(factory);

        foreach (var caller in new[] { Caller.Anonymous, Caller.OtherUser })
        {
            var totals = await GetAsync(_client, Url(owner.User.Id, endpoint), caller, owner, other);
            var profile = await GetAsync(_client, $"/api/Users/{owner.User.Id}", caller, owner, other);

            Assert.Equal(HttpStatusCode.Forbidden, totals.StatusCode);
            // The same answer the profile itself gives that caller.
            Assert.Equal(profile.StatusCode, totals.StatusCode);
            // The field that carries the figure, not the figure's digits: a 403 body holds a
            // random trace id, which can contain any short digit string.
            Assert.DoesNotContain("\"stat\"", await totals.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task PrivateProfile_TotalsAnswerTheOwner(string endpoint)
    {
        var owner = await OwnerWithOnePrintAsync(User.ProfileViewStatus.Private);

        foreach (var caller in new[] { Caller.Owner, Caller.OwnerApiKey })
        {
            var body = await GetJsonAsync(_client, Url(owner.User.Id, endpoint), caller, owner);
            Assert.Equal(Expected(endpoint), body.GetProperty("stat").GetString());
        }
    }

    [Theory]
    [InlineData(User.ProfileViewStatus.Public, "print-count")]
    [InlineData(User.ProfileViewStatus.Public, "total-print-time")]
    [InlineData(User.ProfileViewStatus.Public, "total-filament-usage")]
    [InlineData(User.ProfileViewStatus.Unlisted, "print-count")]
    [InlineData(User.ProfileViewStatus.Unlisted, "total-print-time")]
    [InlineData(User.ProfileViewStatus.Unlisted, "total-filament-usage")]
    public async Task VisibleProfile_TotalsAnswerEveryone(User.ProfileViewStatus profile, string endpoint)
    {
        var owner = await OwnerWithOnePrintAsync(profile);
        var other = await OtherUserAsync(factory);

        foreach (var caller in new[] { Caller.Anonymous, Caller.OtherUser, Caller.Owner })
        {
            JsonElement body = await GetJsonAsync(_client, Url(owner.User.Id, endpoint), caller, owner, other);
            Assert.Equal(Expected(endpoint), body.GetProperty("stat").GetString());
        }
    }
}
