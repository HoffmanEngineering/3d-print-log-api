using System.Net;
using PrintLogApi.Models;
using Xunit;
using static PrintLogApi.IntegrationTests.Controllers.PublicReadSeed;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// <c>GET /api/Users/{id}/summary</c> follows the profile's visibility, as <c>GET /api/Users/{id}</c>
/// does. It was anonymous with no check, so walking ids gave the display name and pictures of every
/// Private and Friends profile. The print page treats a failed summary as "no byline" (#66).
/// </summary>
public class UsersSummaryVisibilityTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(long userId) => $"/api/Users/{userId}/summary";

    [Theory]
    [InlineData(User.ProfileViewStatus.Private)]
    [InlineData(User.ProfileViewStatus.Friends)]
    public async Task HiddenProfile_SummaryIsForbidden_ToAnonymousAndToOtherUsers(User.ProfileViewStatus view)
    {
        var owner = await OwnerAsync(factory, view);
        var other = await OtherUserAsync(factory);

        foreach (var caller in new[] { Caller.Anonymous, Caller.OtherUser })
        {
            var summary = await GetAsync(_client, Url(owner.User.Id), caller, owner, other);
            var profile = await GetAsync(_client, $"/api/Users/{owner.User.Id}", caller, owner, other);

            Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);
            // The same answer the profile itself gives that caller.
            Assert.Equal(profile.StatusCode, summary.StatusCode);
            Assert.DoesNotContain(owner.User.DisplayName!, await summary.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PrivateProfile_SummaryAnswersTheOwner()
    {
        var owner = await OwnerAsync(factory, User.ProfileViewStatus.Private);

        var body = await GetJsonAsync(_client, Url(owner.User.Id), Caller.Owner, owner);

        Assert.Equal(owner.User.Id, body.GetProperty("id").GetInt64());
        Assert.Equal(owner.User.DisplayName, body.GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData(User.ProfileViewStatus.Public)]
    [InlineData(User.ProfileViewStatus.Unlisted)]
    public async Task VisibleProfile_SummaryAnswersEveryone(User.ProfileViewStatus view)
    {
        var owner = await OwnerAsync(factory, view);
        var other = await OtherUserAsync(factory);

        foreach (var caller in new[] { Caller.Anonymous, Caller.OtherUser, Caller.Owner })
        {
            var body = await GetJsonAsync(_client, Url(owner.User.Id), caller, owner, other);
            Assert.Equal(owner.User.DisplayName, body.GetProperty("displayName").GetString());
        }
    }
}
