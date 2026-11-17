using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// A connector's file identity (printlog-bridge#40): sent as <c>fileHash</c> on create and on
/// claim, and stored in <see cref="Print.FileHash"/>, so prints of the same file can be told apart
/// from prints of a re-sliced one. Sent as raw JSON, so the wire name is what is tested.
/// </summary>
public class PrintsControllerFileHashTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient = factory.CreateClient();

    private static readonly byte[] Identity = Enumerable.Range(0, 20).Select(i => (byte)(i * 11 + 3)).ToArray();

    [Fact]
    public async Task PostPrint_WithFileHash_StoresItsBytes()
    {
        var created = await ReadAsync(await PostAsync(NewPrint(fileHash: Convert.ToHexStringLower(Identity))));

        Assert.Equal(Identity, (await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task PostPrint_WithUpperCaseFileHash_StoresTheSameBytes()
    {
        var created = await ReadAsync(await PostAsync(NewPrint(fileHash: Convert.ToHexString(Identity))));

        Assert.Equal(Identity, (await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task PostPrint_WithoutFileHash_StoresNone()
    {
        var created = await ReadAsync(await PostAsync(NewPrint()));

        Assert.Null((await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task PostPrint_FileHash_IsReadBackByTheClaimLookup()
    {
        // Lowercase hex, the format the claim lookup has always given an OctoPrint SHA-1 in.
        var created = await ReadAsync(await PostAsync(NewPrint(fileHash: Convert.ToHexString(Identity))));

        var claimable = await GetClaimableAsync($"printerId={IntegrationTestSeeder.TestPrinterId}");

        Assert.Equal(Convert.ToHexStringLower(Identity), claimable.Single(p => p.Id == created.Id).FileHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("00112233445566778899aabbccddeeff")] // 16 bytes: a bare uuid does not fit
    [InlineData("00112233445566778899aabbccddeeff0011223344")] // 21 bytes
    [InlineData("zz112233445566778899aabbccddeeff00112233")]
    public async Task PostPrint_MalformedFileHash_Returns400(string fileHash)
    {
        var response = await PostAsync(NewPrint(fileHash: fileHash));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostPrint_ExplicitNullFileHash_StoresNone()
    {
        var body = NewPrint();
        body["fileHash"] = null;

        var created = await ReadAsync(await PostAsync(body));

        Assert.Null((await LoadPrintAsync(created.Id)).FileHash);
    }

    [Theory]
    [InlineData("0x00112233445566778899aabbccddeeff00112233")] // 0x and 20 bytes
    [InlineData("0x112233445566778899aabbccddeeff00112233")] // 0x and 40 characters in all
    public async Task PostPrint_HexPrefixedFileHash_Returns400(string fileHash)
    {
        var response = await PostAsync(NewPrint(fileHash: fileHash));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostPrint_TooLongFileHash_SaysWhatAFileHashIs()
    {
        // One rule, one message: not a length limit first and the format after.
        var response = await PostAsync(NewPrint(fileHash: Convert.ToHexStringLower(Identity) + "00"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(PrintFileHashes.ValidationMessage, problem);
        Assert.DoesNotContain("length", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostPrint_ReplayedPair_KeepsTheStoredHash()
    {
        var body = NewPrint(fileHash: Convert.ToHexStringLower(Identity));
        body["externalSource"] = "moonraker";
        body["externalId"] = $"{Guid.NewGuid()}:{Random.Shared.Next(1, 100000)}";
        var created = await ReadAsync(await PostAsync(body));

        body["fileHash"] = Convert.ToHexStringLower(Enumerable.Repeat((byte)0xCD, 20).ToArray());
        var replay = await PostAsync(body);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(created.Id, (await ReadAsync(replay)).Id);
        Assert.Equal(Identity, (await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task Claim_ReplayedPair_KeepsTheStoredHash()
    {
        var created = await ReadAsync(await PostAsync(NewPrint()));
        var claim = Claim(Convert.ToHexStringLower(Identity));
        await ReadAsync(await ClaimAsync(created.Id, claim));

        claim["fileHash"] = Convert.ToHexStringLower(Enumerable.Repeat((byte)0xCD, 20).ToArray());
        var replay = await ClaimAsync(created.Id, claim);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(Identity, (await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task Claim_WithFileHash_FillsAMissingOne()
    {
        var created = await ReadAsync(await PostAsync(NewPrint()));

        await ReadAsync(await ClaimAsync(created.Id, Claim(Convert.ToHexStringLower(Identity))));

        Assert.Equal(Identity, (await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task Claim_WithFileHash_KeepsTheOneThePrintHas()
    {
        // An OctoPrint SHA-1 of the file says more than a connector's identity for it.
        var own = Enumerable.Repeat((byte)0xAB, 20).ToArray();
        var created = await ReadAsync(await PostAsync(NewPrint(fileHash: Convert.ToHexStringLower(own))));

        await ReadAsync(await ClaimAsync(created.Id, Claim(Convert.ToHexStringLower(Identity))));

        Assert.Equal(own, (await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task Claim_WithoutFileHash_KeepsTheOneThePrintHas()
    {
        var created = await ReadAsync(await PostAsync(NewPrint(fileHash: Convert.ToHexStringLower(Identity))));

        await ReadAsync(await ClaimAsync(created.Id, Claim(fileHash: null)));

        Assert.Equal(Identity, (await LoadPrintAsync(created.Id)).FileHash);
    }

    [Fact]
    public async Task Claim_MalformedFileHash_Returns400AndClaimsNothing()
    {
        var created = await ReadAsync(await PostAsync(NewPrint()));

        var response = await ClaimAsync(created.Id, Claim("not-a-hash"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var print = await LoadPrintAsync(created.Id);
        Assert.Null(print.ExternalId);
        Assert.Null(print.FileHash);
    }

    private static Dictionary<string, object?> NewPrint(string? fileHash = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = $"file hash {Guid.NewGuid():N}",
            ["printerId"] = IntegrationTestSeeder.TestPrinterId,
            ["status"] = (int)PrintStatus.Printing,
            ["viewStatus"] = (int)PrintViewStatus.Private,
            ["filamentUsage"] = Array.Empty<object>(),
        };
        if (fileHash is not null)
        {
            body["fileHash"] = fileHash;
        }
        return body;
    }

    private static Dictionary<string, object?> Claim(string? fileHash)
    {
        var body = new Dictionary<string, object?>
        {
            ["externalSource"] = "moonraker",
            ["externalId"] = $"{Guid.NewGuid()}:{Random.Shared.Next(1, 100000)}",
        };
        if (fileHash is not null)
        {
            body["fileHash"] = fileHash;
        }
        return body;
    }

    private async Task<HttpResponseMessage> PostAsync(Dictionary<string, object?> body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> ClaimAsync(long id, Dictionary<string, object?> body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{id}/claim");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<List<ClaimablePrintDto>> GetClaimableAsync(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/claimable?{query}&limit=100");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<List<ClaimablePrintDto>>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<Print> LoadPrintAsync(long id)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await context.Prints.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
    }
}
