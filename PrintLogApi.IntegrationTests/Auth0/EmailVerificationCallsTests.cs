using System.Net;
using System.Text.Json;
using PrintLogApi.Exceptions;
using Xunit;

namespace PrintLogApi.IntegrationTests.Auth0;

public class EmailVerificationCallsTests
{
    private const string UserId = "auth0|abc123";

    private static StubHandler HandlerReturning(Func<HttpRequestMessage, HttpResponseMessage> apiResponder)
    {
        return new StubHandler
        {
            Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
                ? Auth0TestHarness.TokenResponse()
                : apiResponder(request),
        };
    }

    [Fact]
    public async Task GetEmailStatus_ParsesVerifiedFlag()
    {
        var handler = HandlerReturning(_ =>
            Auth0TestHarness.Json("{\"email\":\"a@example.com\",\"email_verified\":true}"));
        var service = Auth0TestHarness.CreateService(handler);

        var status = await service.GetEmailStatus(UserId, TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.Equal("a@example.com", status.Email);
        Assert.True(status.EmailVerified);
        var request = handler.Requests.Single(r => !r.RequestUri!.AbsolutePath.EndsWith("/oauth/token"));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("fields=email,email_verified", request.RequestUri!.Query);
        Assert.Contains("include_fields=true", request.RequestUri.Query);
        Assert.EndsWith("/users/" + Uri.EscapeDataString(UserId), request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetEmailStatus_EmptyBody_ReturnsNull()
    {
        var handler = HandlerReturning(_ => Auth0TestHarness.Json(""));
        var service = Auth0TestHarness.CreateService(handler);

        var status = await service.GetEmailStatus(UserId, TestContext.Current.CancellationToken);

        Assert.Null(status);
    }

    [Fact]
    public async Task SendVerificationEmail_PostsUserId()
    {
        var bodies = new List<string>();
        var handler = HandlerReturning(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Auth0TestHarness.Json("{\"status\":\"pending\"}");
        });
        var service = Auth0TestHarness.CreateService(handler);

        await service.SendVerificationEmail(UserId, TestContext.Current.CancellationToken);

        var request = handler.Requests.Single(r => !r.RequestUri!.AbsolutePath.EndsWith("/oauth/token"));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v2/jobs/verification-email", request.RequestUri!.AbsolutePath);
        var body = Assert.Single(bodies);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(UserId, doc.RootElement.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task SendVerificationEmail_RetriesOnceAfter401WithBody()
    {
        var bodies = new List<string>();
        var calls = 0;
        var handler = HandlerReturning(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Auth0TestHarness.Json("{\"status\":\"pending\"}");
        });
        var service = Auth0TestHarness.CreateService(handler);

        await service.SendVerificationEmail(UserId, TestContext.Current.CancellationToken);

        Assert.Equal(2, bodies.Count);
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(UserId, doc.RootElement.GetProperty("user_id").GetString());
        }
    }

    [Fact]
    public async Task SendVerificationEmail_Non2xx_Throws()
    {
        var handler = HandlerReturning(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var service = Auth0TestHarness.CreateService(handler);

        await Assert.ThrowsAsync<Auth0ApiException>(() =>
            service.SendVerificationEmail(UserId, TestContext.Current.CancellationToken));
    }
}
