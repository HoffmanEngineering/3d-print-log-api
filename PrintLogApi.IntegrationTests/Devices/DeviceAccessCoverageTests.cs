using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing.Patterns;
using PrintLogApi.Authentication.Devices;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// The structural half of device-token scoping (#148). A device token is a long-lived credential
/// in a file on a printer host, so where it works is an allow-list: an action opts in with
/// <see cref="DeviceAccessAttribute"/>, and everything else refuses it. These tests walk every
/// endpoint the host maps, so a new controller or action is covered the day it is added.
/// </summary>
public partial class DeviceAccessCoverageTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    /// <summary>
    /// Every endpoint a device token may reach, with the scope it needs ("*" for any grant). This
    /// list is the review point: adding a row widens what a paired device can do.
    /// </summary>
    private static readonly SortedSet<string> ExpectedAllowList = new(StringComparer.Ordinal)
    {
        "GET api/Users/me *",
        "GET api/Users/me/user-settings *",
        "GET api/Subscription/me *",
        "GET api/Printers/summary printers:read",
        "POST api/Printers printers:create",
        "GET api/Printers/{id} printers:read",
        "GET api/Printers/{id}/filament printers:read",
        "PUT api/Printers/{id} printers:slots",
        "PUT api/Printers/{id}/slots/{slot} printers:slots",
        "DELETE api/Printers/{id}/slots/{slot} printers:slots",
        "GET api/Filaments/by-tag/{uid} filaments:read",
        "GET api/Filaments/by-number/{spoolNumber:int} filaments:read",
        "POST api/Prints prints:write",
        "GET api/Prints/claimable prints:read",
        "GET api/Prints/{id} prints:read",
        "PATCH api/Prints/{id} prints:write",
        "POST api/Prints/{id}/claim prints:write",
        "POST api/Prints/{id}/complete prints:write",
        "POST api/Prints/{id}/image prints:write",
        "POST api/Prints/{id}/slicer-settings prints:write",
        "GET api/Prints/{id}/files prints:read",
        "POST api/Prints/{id}/files/upload-url files:write",
        "POST api/Prints/{id}/files/confirm files:write",
        "PUT api/Connections/{instanceId} connections:write",
    };

    private IEnumerable<(RouteEndpoint Endpoint, string Method)> ApiEndpoints()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var template = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!template.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                yield return (endpoint, method);
            }
        }
    }

    private static string Key(RouteEndpoint endpoint, string method)
    {
        var access = endpoint.Metadata.GetMetadata<DeviceAccessAttribute>()!;
        return $"{method} {endpoint.RoutePattern.RawText!.TrimStart('/')} {access.Scope ?? "*"}";
    }

    [Fact]
    public void TheAllowList_IsExactlyTheConnectorEndpoints()
    {
        var actual = new SortedSet<string>(
            ApiEndpoints()
                .Where(e => e.Endpoint.Metadata.GetMetadata<DeviceAccessAttribute>() is not null)
                .Select(e => Key(e.Endpoint, e.Method)),
            StringComparer.Ordinal);

        Assert.Equal(ExpectedAllowList, actual);
    }

    [Fact]
    public void EveryScopeNamedOnAnEndpoint_IsADeviceScope()
    {
        var named = ApiEndpoints()
            .Select(e => e.Endpoint.Metadata.GetMetadata<DeviceAccessAttribute>()?.Scope)
            .OfType<string>();

        Assert.All(named, scope => Assert.Contains(scope, DeviceScopes.All));
    }

    /// <summary>
    /// The opt-in is per action. A class-level attribute would open every action a controller
    /// grows later, which is exactly the drift the allow-list exists to stop.
    /// </summary>
    [Fact]
    public void DeviceAccess_CanOnlyBePutOnAnAction()
    {
        var usage = typeof(DeviceAccessAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
        Assert.False(usage.Inherited && usage.AllowMultiple);
    }

    /// <summary>
    /// An allowed endpoint must authorize through the default policy, which accepts the principal
    /// the device middleware set. A policy that pins a scheme (InteractiveUserOnly) would
    /// re-authenticate and drop it, so the attribute there would be a lie.
    /// </summary>
    [Fact]
    public void NoAllowedEndpoint_PinsASchemeOrPolicy()
    {
        var offenders = ApiEndpoints()
            .Where(e => e.Endpoint.Metadata.GetMetadata<DeviceAccessAttribute>() is not null)
            .Where(e => e.Endpoint.Metadata.OfType<IAuthorizeData>().Any(a => a.Policy is not null || a.AuthenticationSchemes is not null || a.Roles is not null))
            .Select(e => Key(e.Endpoint, e.Method))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Sends a full-scope device token to every <c>/api</c> endpoint the host maps. Every one
    /// outside the allow-list must answer 403 with the device code, before any model binding or
    /// handler runs; every one on it must get past the device check.
    /// </summary>
    [Fact]
    public async Task EveryApiEndpoint_RefusesADeviceToken_UnlessItIsOnTheAllowList()
    {
        var client = factory.CreateClient();
        var token = await TokenAsync(client, NewSubject("walk"), DeviceScopes.All);

        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var (endpoint, method) in ApiEndpoints())
        {
            var url = "/" + Concrete(endpoint.RoutePattern);
            var request = WithDevice(new HttpRequestMessage(new HttpMethod(method), url), token);
            if (method is "POST" or "PUT" or "PATCH")
            {
                // The content type the action consumes. With the wrong one, routing picks its own
                // 415 endpoint, which carries no metadata and so is (correctly) refused as well.
                var consumes = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>()?.ContentTypes ?? [];
                request.Content = consumes.Any(c => c.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                    ? new MultipartFormDataContent { { new StringContent("x"), "unused" } }
                    : JsonContent.Create(new { });
            }

            var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            var code = await CodeOf(response);
            var allowed = endpoint.Metadata.GetMetadata<DeviceAccessAttribute>() is not null;
            checkedCount++;

            if (!allowed && (response.StatusCode != HttpStatusCode.Forbidden || code != DeviceTokenErrors.EndpointNotAllowed))
            {
                failures.Add($"{method} {url} ({endpoint.DisplayName}): {(int)response.StatusCode} {code}");
            }

            if (allowed && (code is DeviceTokenErrors.EndpointNotAllowed or DeviceTokenErrors.InsufficientScope or DeviceTokenErrors.InvalidToken
                || response.StatusCode is HttpStatusCode.Unauthorized))
            {
                failures.Add($"allowed {method} {url}: {(int)response.StatusCode} {code}");
            }
        }

        Assert.Empty(failures);

        // A walk that matched nothing would pass vacuously (there were 183 when this was written).
        Assert.True(checkedCount > 150, $"only {checkedCount} endpoints walked");
    }

    /// <summary>Fills every route parameter with a value its constraint accepts.</summary>
    private static string Concrete(RoutePattern pattern)
    {
        var template = pattern.RawText!.TrimStart('/');
        return Parameter().Replace(template, match =>
        {
            var body = match.Groups[1].Value;
            if (body.Contains(":guid", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.NewGuid().ToString();
            }

            if (body.Contains(":bool", StringComparison.OrdinalIgnoreCase))
            {
                return "true";
            }

            return "1";
        });
    }

    [GeneratedRegex(@"\{\**([^}]+)\}")]
    private static partial Regex Parameter();
}
