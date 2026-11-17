using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.User;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Profile and cover pictures live in the <c>userprofile</c> container, one blob and one
/// <c>Files</c> row per upload. A picture that is replaced or removed, and every picture left at
/// account deletion, has its blob deleted. Only blobs the user's own <c>Files</c> rows name are
/// touched: a picture URL that points anywhere else (the Auth0 picture, another user's blob) is
/// never passed to blob deletion, and a failed blob delete never fails the request.
/// </summary>
public class UserPictureBlobLifecycleTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    private const string Container = "userprofile";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InMemoryBlobStorageService Blobs
        => (InMemoryBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();

    [Theory]
    [InlineData("profile-image")]
    [InlineData("cover-image")]
    public async Task ReplacingAPicture_DeletesTheOldBlobAndItsFileRow(string endpoint)
    {
        var user = await CreateUserAsync();
        var client = factory.CreateClient();

        var first = await UploadAsync(client, user, endpoint);
        var second = await UploadAsync(client, user, endpoint);

        Assert.False(Blobs.BlobExists(first), first);
        Assert.True(Blobs.BlobExists(second), second);
        Assert.Equal([second], await FilePathsAsync(user.Id));
    }

    [Fact]
    public async Task RemovingTheCoverPicture_DeletesItsBlobAndFileRow()
    {
        var user = await CreateUserAsync();
        var client = factory.CreateClient();
        var cover = await UploadAsync(client, user, "cover-image");

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/Users/me/cover-image");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Blobs.BlobExists(cover), cover);
        Assert.Empty(await FilePathsAsync(user.Id));
    }

    /// <summary>
    /// The Auth0 picture is an external URL, and a legacy row might hold another user's blob URL.
    /// Neither is the user's to delete.
    /// </summary>
    [Fact]
    public async Task ReplacingAPictureThatIsNotTheUsersOwnBlob_DeletesNothing()
    {
        var other = await CreateUserAsync();
        var client = factory.CreateClient();
        var othersBlob = await UploadAsync(client, other, "profile-image");
        var user = await CreateUserAsync(
            profilePicture: "https://s.gravatar.com/avatar/0123456789abcdef?s=480&d=identicon",
            coverPicture: new Uri(Blobs.BaseUri, othersBlob).AbsoluteUri);
        var deletesBefore = Blobs.DeletedBlobNames.Count;

        await UploadAsync(client, user, "profile-image");
        await UploadAsync(client, user, "cover-image");

        Assert.Equal(deletesBefore, Blobs.DeletedBlobNames.Count);
        Assert.True(Blobs.BlobExists(othersBlob));
        Assert.Equal([othersBlob], await FilePathsAsync(other.Id));
    }

    [Fact]
    public async Task ABlobDeleteThatFails_DoesNotFailTheUpload_AndKeepsTheFileRow()
    {
        var failing = new FailingDeleteBlobStorageService(Blobs);
        var client = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IBlobStorageService>(failing)))
            .CreateClient();
        var user = await CreateUserAsync();
        var first = await UploadAsync(client, user, "profile-image");

        var second = await UploadAsync(client, user, "profile-image");

        Assert.True(Blobs.BlobExists(first));
        // The row stays, so account deletion still finds the blob.
        Assert.Equal(new[] { first, second }.Order(), (await FilePathsAsync(user.Id)).Order());
        Assert.Equal(1, failing.FailedDeletes);
    }

    /// <summary>
    /// Account deletion deletes every userprofile blob the user's Files rows name: the current
    /// pictures and the replaced ones left by older uploads. Another user's are untouched.
    /// </summary>
    [Fact]
    public async Task AccountDeletion_DeletesTheUsersPictureBlobs_AndNoOneElses()
    {
        var other = await CreateUserAsync();
        var client = factory.CreateClient();
        var othersBlob = await UploadAsync(client, other, "profile-image");
        var user = await CreateUserAsync();
        var profile = await UploadAsync(client, user, "profile-image");
        var cover = await UploadAsync(client, user, "cover-image");
        var leftByAnOlderUpload = await SeedOrphanedPictureAsync(user.Id);

        await DeleteAccountAsync(factory.Services, user.Id);

        Assert.All([profile, cover, leftByAnOlderUpload], p => Assert.False(Blobs.BlobExists(p), p));
        Assert.True(Blobs.BlobExists(othersBlob));
        Assert.Equal([othersBlob], await FilePathsAsync(other.Id));
    }

    [Fact]
    public async Task AccountDeletion_WhenABlobDeleteFails_StillDeletesTheAccount()
    {
        var failing = new FailingDeleteBlobStorageService(Blobs);
        var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IBlobStorageService>(failing)));
        var user = await CreateUserAsync();
        var profile = await UploadAsync(host.CreateClient(), user, "profile-image");

        await DeleteAccountAsync(host.Services, user.Id);

        Assert.Equal(1, failing.FailedDeletes);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Users.AnyAsync(u => u.Id == user.Id, Ct));
        Assert.True(Blobs.BlobExists(profile));
    }

    // ---- Helpers ----

    private async Task<User> CreateUserAsync(string? profilePicture = null, string? coverPicture = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|picture-{Guid.NewGuid():N}",
            ViewStatus = User.ProfileViewStatus.Public,
            ProfilePicture = profilePicture,
            CoverPicture = coverPicture,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    /// <summary>Uploads a picture as <paramref name="user"/>; returns its blob path ("userprofile/{name}").</summary>
    private static async Task<string> UploadAsync(HttpClient client, User user, string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Users/me/{endpoint}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        var content = new ByteArrayContent(PngBytes());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        request.Content = new MultipartFormDataContent { { content, "image", "picture.png" } };

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<UserUrlDto>(Ct))?.Url;
        Assert.NotNull(url);
        return new Uri(url).AbsolutePath.TrimStart('/');
    }

    private async Task<string> SeedOrphanedPictureAsync(long userId)
    {
        var name = $"{Guid.NewGuid()}.jpg";
        using (var stream = new MemoryStream([1, 2, 3]))
        {
            await Blobs.UploadAsync(Container, name, stream);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        db.Files.Add(new Models.File { Id = Guid.NewGuid(), Path = $"{Container}/{name}", Size = 3, CreatedById = userId, UpdatedById = userId });
        await db.SaveChangesAsync(Ct);
        return $"{Container}/{name}";
    }

    private async Task<List<string>> FilePathsAsync(long userId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Files
            .Where(f => f.CreatedById == userId)
            .Select(f => f.Path!)
            .ToListAsync(Ct);
    }

    private static async Task DeleteAccountAsync(IServiceProvider services, long userId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId, Ct);
        await scope.ServiceProvider.GetRequiredService<IUserDeletionService>().DeleteAllDataForUser(user);
    }

    private static byte[] PngBytes()
    {
        using var bitmap = new SKBitmap(32, 32);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>The in-memory store, except that every delete throws, as a storage outage would.</summary>
    private sealed class FailingDeleteBlobStorageService(InMemoryBlobStorageService inner) : IBlobStorageService
    {
        public int FailedDeletes { get; private set; }

        public Task DeleteBlobAsync(string containerName, string blobName)
        {
            FailedDeletes++;
            throw new InvalidOperationException("Simulated storage outage.");
        }

        public Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream)
            => inner.UploadAsync(containerName, blobName, stream);

        public Task<BlobUploadResult> UploadAsync(string containerName, string blobName, Stream stream, IReadOnlyDictionary<string, string> metadata)
            => inner.UploadAsync(containerName, blobName, stream, metadata);

        public IAsyncEnumerable<StoredBlob> ListBlobsAsync(string containerName, CancellationToken cancellationToken = default)
            => inner.ListBlobsAsync(containerName, cancellationToken);

        public Task<Uri> GenerateSasUploadUrlAsync(string containerName, string blobName, TimeSpan expiry)
            => inner.GenerateSasUploadUrlAsync(containerName, blobName, expiry);

        public Task<Uri> GenerateSasDownloadUrlAsync(string containerName, string blobName, string contentType, string originalFileName, TimeSpan expiry)
            => inner.GenerateSasDownloadUrlAsync(containerName, blobName, contentType, originalFileName, expiry);

        public Task<Uri> GenerateSasInlineUrlAsync(string containerName, string blobName, string contentType, TimeSpan bucketSize, TimeSpan cacheControlMaxAge)
            => inner.GenerateSasInlineUrlAsync(containerName, blobName, contentType, bucketSize, cacheControlMaxAge);

        public Task<(Stream stream, string fileName)?> DownloadAsync(string containerName, string blobName)
            => inner.DownloadAsync(containerName, blobName);

        public Task<BlobContent?> OpenReadAsync(string containerName, string blobName, bool seekable, CancellationToken cancellationToken = default)
            => inner.OpenReadAsync(containerName, blobName, seekable, cancellationToken);

        public Task<long?> GetBlobLengthAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
            => inner.GetBlobLengthAsync(containerName, blobName, cancellationToken);
    }
}
