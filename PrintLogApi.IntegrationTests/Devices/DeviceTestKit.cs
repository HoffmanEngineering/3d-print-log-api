using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.IntegrationTests.Mcp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// Pairs devices the way printlog-bridge will (#148): an Auth0 device-flow access token, minted
/// here with the local signing key, exchanged once at <c>POST /api/device-grants</c>.
/// </summary>
public static class DeviceTestKit
{
    public static readonly string[] BridgeScopes =
    [
        DeviceScopes.PrintsRead, DeviceScopes.PrintsWrite, DeviceScopes.PrintersRead,
        DeviceScopes.PrintersSlots, DeviceScopes.FilamentsRead, DeviceScopes.ConnectionsWrite,
    ];

    /// <summary>An access token as Auth0 issues it to the bridge's Native app for the device audience.</summary>
    public static string PairingToken(
        string subject,
        IEnumerable<string>? scopes = null,
        string audience = TestJwt.DeviceAudience,
        string? clientId = TestJwt.DeviceClientId,
        DateTime? issuedAt = null)
        => TestJwt.Create(
            audience,
            subject,
            scopes ?? BridgeScopes,
            extraClaims: clientId is null ? null : [new Claim("azp", clientId)],
            issuedAt: issuedAt);

    public static HttpRequestMessage PairRequest(string pairingToken, Guid deviceId, string? name = "printlog-bridge on voron")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/device-grants")
        {
            Content = JsonContent.Create(new { deviceId, name }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairingToken);
        return request;
    }

    /// <summary>Pairs a new device for <paramref name="subject"/> (created on first sight) and returns the pairing response.</summary>
    public static async Task<JsonElement> PairAsync(
        HttpClient client,
        string subject,
        IEnumerable<string>? scopes = null,
        Guid? deviceId = null,
        string name = "printlog-bridge on voron")
    {
        var response = await client.SendAsync(
            PairRequest(PairingToken(subject, scopes), deviceId ?? Guid.NewGuid(), name),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"pairing failed: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Pairs and returns just the device token.</summary>
    public static async Task<string> TokenAsync(HttpClient client, string subject, IEnumerable<string>? scopes = null)
        => (await PairAsync(client, subject, scopes)).GetProperty("token").GetString()!;

    /// <summary>Creates a printer for an existing user through the web app's endpoint and returns its id.</summary>
    public static async Task<long> CreatePrinterAsync(HttpClient client, string oauthUserId)
    {
        var request = AsUser(new HttpRequestMessage(HttpMethod.Post, "/api/Printers")
        {
            Content = JsonContent.Create(new PrintLogApi.Models.DTOs.Printer.AddPrinterDTO
            {
                Name = $"Device test printer {Guid.NewGuid():N}",
                Make = "Make",
                Model = "Model",
                IsActive = true,
            }),
        }, oauthUserId);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt64();
    }

    /// <summary>A print create a bridge would send, for a printer of the device's user.</summary>
    public static HttpRequestMessage PrintCreate(string token, long printerId, string title = "Bridge print")
        => WithDevice(new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new { Title = title, PrinterId = printerId }),
        }, token);

    public static HttpRequestMessage WithDevice(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public static HttpRequestMessage AsUser(HttpRequestMessage request, string oauthUserId)
    {
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthUserId);
        return request;
    }

    /// <summary>The problem body's <c>code</c>, or null.</summary>
    public static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string NewSubject(string label) => $"auth0|device-{label}-{Guid.NewGuid():N}";
}
