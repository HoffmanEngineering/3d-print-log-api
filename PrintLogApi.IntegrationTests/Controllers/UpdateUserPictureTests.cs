using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Models.DTOs.User;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// <c>PUT /api/Users/me</c> and the two picture fields. Pictures are shown to every visitor of a
/// profile, print or comment, so an arbitrary URL there is a tracking pixel on someone else's
/// host. The upload endpoints are the writers; PUT accepts only what the web app sends: the
/// stored values sent back unchanged (Settings spreads the loaded profile into its PUT), and, at
/// first sign-in, the picture on the user's Auth0 profile.
/// </summary>
public class UpdateUserPictureTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string Uploaded = "https://example.blob.core.windows.net/userprofile/uploaded.png";
    private const string UploadedCover = "https://example.blob.core.windows.net/userprofile/cover.png";
    private const string Foreign = "https://attacker.example/pixel.gif";
    private const string Gravatar = "https://s.gravatar.com/avatar/abc?s=480&r=pg&d=https%3A%2F%2Fcdn.auth0.com%2Favatars%2Fma.png";

    private readonly CustomWebApplicationFactory _factory;

    public UpdateUserPictureTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ForeignProfilePicture_IsIgnored()
    {
        var user = await CreateUserAsync(Uploaded, UploadedCover);

        var response = await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "Maker",
            ProfilePicture = Foreign,
            CoverPicture = UploadedCover,
            ViewStatus = User.ProfileViewStatus.Public,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadAsync(user.Id);
        Assert.Equal(Uploaded, stored.ProfilePicture);
        Assert.Equal("Maker", stored.DisplayName);
    }

    [Fact]
    public async Task ForeignCoverPicture_IsIgnored()
    {
        var user = await CreateUserAsync(Uploaded, UploadedCover);

        await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "Maker",
            ProfilePicture = Uploaded,
            CoverPicture = Foreign,
            ViewStatus = User.ProfileViewStatus.Public,
        });

        Assert.Equal(UploadedCover, (await LoadAsync(user.Id)).CoverPicture);
    }

    // A client that sends only the fields it changes must not wipe the pictures.
    [Fact]
    public async Task OmittedPictures_AreKept()
    {
        var user = await CreateUserAsync(Uploaded, UploadedCover);

        await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "Maker",
            ViewStatus = User.ProfileViewStatus.Unlisted,
        });

        var stored = await LoadAsync(user.Id);
        Assert.Equal(Uploaded, stored.ProfilePicture);
        Assert.Equal(UploadedCover, stored.CoverPicture);
        Assert.Equal(User.ProfileViewStatus.Unlisted, stored.ViewStatus);
    }

    // Settings: the loaded profile, pictures included, comes back with a new visibility.
    [Fact]
    public async Task RoundTrippedPictures_AreKept_AndOtherFieldsSaved()
    {
        var user = await CreateUserAsync(Uploaded, UploadedCover);

        var response = await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "Maker",
            Bio = "Prints things",
            ProfilePicture = Uploaded,
            CoverPicture = UploadedCover,
            ViewStatus = User.ProfileViewStatus.Private,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<UserDetailDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(Uploaded, body.ProfilePicture);
        var stored = await LoadAsync(user.Id);
        Assert.Equal((Uploaded, UploadedCover, User.ProfileViewStatus.Private, "Prints things"),
            (stored.ProfilePicture, stored.CoverPicture, stored.ViewStatus, stored.Bio));
    }

    // First sign-in: the web app sets the Auth0 picture (Gravatar or the social avatar).
    [Fact]
    public async Task FirstSignIn_Auth0Picture_IsSaved()
    {
        var user = await CreateUserAsync(null, null);
        _factory.Auth0.UserPictures[user.OAuthUserId!] = Gravatar;

        var response = await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "newmaker",
            ProfilePicture = Gravatar,
            ViewStatus = User.ProfileViewStatus.Public,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<UserDetailDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(Gravatar, body.ProfilePicture);
        Assert.Equal(Gravatar, (await LoadAsync(user.Id)).ProfilePicture);
    }

    [Fact]
    public async Task FirstSignIn_PictureThatIsNotTheAuth0One_IsIgnored()
    {
        var user = await CreateUserAsync(null, null);
        _factory.Auth0.UserPictures[user.OAuthUserId!] = Gravatar;

        var response = await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "newmaker",
            ProfilePicture = Foreign,
            ViewStatus = User.ProfileViewStatus.Public,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadAsync(user.Id);
        Assert.Null(stored.ProfilePicture);
        Assert.Equal("newmaker", stored.DisplayName);
    }

    // Auth0 down: the rest of the update still saves; the picture is not taken on trust.
    [Fact]
    public async Task FirstSignIn_Auth0Unavailable_SavesTheRest()
    {
        var user = await CreateUserAsync(null, null);
        _factory.Auth0.FailUserPicture[user.OAuthUserId!] = true;

        var response = await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "newmaker",
            ProfilePicture = Gravatar,
            ViewStatus = User.ProfileViewStatus.Public,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadAsync(user.Id);
        Assert.Null(stored.ProfilePicture);
        Assert.Equal("newmaker", stored.DisplayName);
    }

    // Auth0 is asked only while the profile has no picture, so a stream of PUTs with new URLs
    // cannot turn into a stream of Management API calls.
    [Fact]
    public async Task UserWithAPicture_DoesNotAskAuth0()
    {
        var user = await CreateUserAsync(Uploaded, null);
        _factory.Auth0.UserPictures[user.OAuthUserId!] = Gravatar;
        var before = _factory.Auth0.UserPictureCalls;

        await PutAsync(user, new UpdateUserDetailDto
        {
            DisplayName = "Maker",
            ProfilePicture = Gravatar,
            ViewStatus = User.ProfileViewStatus.Public,
        });

        Assert.Equal(before, _factory.Auth0.UserPictureCalls);
        Assert.Equal(Uploaded, (await LoadAsync(user.Id)).ProfilePicture);
    }

    private async Task<HttpResponseMessage> PutAsync(User user, UpdateUserDetailDto dto)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/Users/me");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        request.Content = JsonContent.Create(dto);
        return await _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<User> CreateUserAsync(string? profilePicture, string? coverPicture)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        user.ProfilePicture = profilePicture;
        user.CoverPicture = coverPicture;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private async Task<User> LoadAsync(long id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);
    }
}
