using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// Using a device token (#148): where it works, where it is refused, and that revoking it is
/// effective on the very next request.
/// </summary>
public class DeviceTokenTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private async Task<long> CreatePrint(string token, string subject)
    {
        var response = await Send(PrintCreate(token, await CreatePrinterAsync(_client, subject)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ADeviceToken_CannotDeleteAPrintOrAFilament()
    {
        var subject = NewSubject("delete");
        var token = await TokenAsync(_client, subject, DeviceScopes.All);
        var printId = await CreatePrint(token, subject);

        var deletePrint = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Delete, $"/api/Prints/{printId}"), token));
        Assert.Equal(HttpStatusCode.Forbidden, deletePrint.StatusCode);
        Assert.Equal(DeviceTokenErrors.EndpointNotAllowed, await CodeOf(deletePrint));

        var deleteFilament = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Delete, $"/api/Filaments/{IntegrationTestSeeder.TestFilamentId1}"), token));
        Assert.Equal(HttpStatusCode.Forbidden, deleteFilament.StatusCode);
        Assert.Equal(DeviceTokenErrors.EndpointNotAllowed, await CodeOf(deleteFilament));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.True(await db.Prints.AnyAsync(p => p.Id == printId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAllowedEndpoint_NeedsItsScope()
    {
        var subject = NewSubject("scope");
        var token = await TokenAsync(_client, subject, [DeviceScopes.PrintsRead]);

        var write = await Send(PrintCreate(token, await CreatePrinterAsync(_client, subject)));

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(DeviceTokenErrors.InsufficientScope, await CodeOf(write));
        Assert.Contains("insufficient_scope", write.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        // The scope it does hold still works, and so does an endpoint any grant may use.
        Assert.Equal(HttpStatusCode.OK, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Prints/claimable?printerId=1"), token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token))).StatusCode);
    }

    [Theory]
    [InlineData("pld_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("pld_")]
    [InlineData("pld_short")]
    public async Task AnUnknownToken_Is401_WithACode(string token)
    {
        var response = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(DeviceTokenErrors.InvalidToken, await CodeOf(response));
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoking_TakesEffectOnTheNextRequest()
    {
        var subject = NewSubject("revoke");
        var paired = await PairAsync(_client, subject);
        var token = paired.GetProperty("token").GetString()!;
        var id = paired.GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.OK, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token))).StatusCode);

        var revoke = await Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{id}"), subject));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var after = await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(DeviceTokenErrors.InvalidToken, await CodeOf(after));

        // Gone from the list too, and a second revoke is a 404.
        var list = await (await Send(AsUser(new HttpRequestMessage(HttpMethod.Get, "/api/device-grants"), subject))).Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0, list.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{id}"), subject))).StatusCode);
    }

    [Fact]
    public async Task AnotherUsersGrant_CannotBeRevoked()
    {
        var paired = await PairAsync(_client, NewSubject("owner"));
        var id = paired.GetProperty("id").GetInt64();

        var response = await Send(AsUser(new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{id}"), IntegrationTestSeeder.TestUserOAuthId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), paired.GetProperty("token").GetString()!))).StatusCode);
    }

    [Fact]
    public async Task TheList_ShowsNameScopesAndLastUsed()
    {
        var subject = NewSubject("list");
        var paired = await PairAsync(_client, subject, scopes: [DeviceScopes.PrintsRead, DeviceScopes.ConnectionsWrite], name: "Workshop bridge");

        var before = await (await Send(AsUser(new HttpRequestMessage(HttpMethod.Get, "/api/device-grants"), subject))).Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, before[0].GetProperty("lastUsedAt").ValueKind);

        await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), paired.GetProperty("token").GetString()!));

        var after = await (await Send(AsUser(new HttpRequestMessage(HttpMethod.Get, "/api/device-grants"), subject))).Content
            .ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var grant = Assert.Single(after.EnumerateArray());
        Assert.Equal(paired.GetProperty("id").GetInt64(), grant.GetProperty("id").GetInt64());
        Assert.Equal("Workshop bridge", grant.GetProperty("name").GetString());
        Assert.Equal([DeviceScopes.ConnectionsWrite, DeviceScopes.PrintsRead], grant.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.InRange(grant.GetProperty("lastUsedAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(paired.GetProperty("deviceId").GetGuid(), grant.GetProperty("deviceId").GetGuid());
    }

    [Fact]
    public async Task ListingAndRevoking_RefuseADeviceToken_AndAnApiKey()
    {
        var subject = NewSubject("list-refuse");
        var paired = await PairAsync(_client, subject, scopes: DeviceScopes.All);
        var token = paired.GetProperty("token").GetString()!;
        var id = paired.GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/device-grants"), token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{id}"), token))).StatusCode);

        var keyResponse = await Send(AsUser(new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys") { Content = JsonContent.Create(new { Description = "device list" }) }, subject));
        var key = (await keyResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("publicKey").GetString()!;

        var listWithKey = new HttpRequestMessage(HttpMethod.Get, "/api/device-grants");
        listWithKey.Headers.Add("X-Api-Key", key);
        Assert.False((await Send(listWithKey)).IsSuccessStatusCode);

        var revokeWithKey = new HttpRequestMessage(HttpMethod.Delete, $"/api/device-grants/{id}");
        revokeWithKey.Headers.Add("X-Api-Key", key);
        Assert.False((await Send(revokeWithKey)).IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Send(WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token))).StatusCode);
    }

    [Fact]
    public async Task ADeviceTokenWithAnApiKey_IsRefused()
    {
        var token = await TokenAsync(_client, NewSubject("two-credentials"));
        var request = WithDevice(new HttpRequestMessage(HttpMethod.Get, "/api/Users/me"), token);
        request.Headers.Add("X-Api-Key", "ANYTHING");

        var response = await Send(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(DeviceTokenErrors.MultipleCredentials, await CodeOf(response));
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/spoolman/api/v1/info")]
    public async Task ADeviceToken_IsNotACredentialOutsideTheRestApi(string path)
    {
        var token = await TokenAsync(_client, NewSubject("outside"), DeviceScopes.All);
        var request = WithDevice(new HttpRequestMessage(path == "/mcp" ? HttpMethod.Post : HttpMethod.Get, path), token);
        if (path == "/mcp")
        {
            request.Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        }

        var response = await Send(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
