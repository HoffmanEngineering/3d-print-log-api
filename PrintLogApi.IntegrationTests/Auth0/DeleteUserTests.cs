using System.Net;
using PrintLogApi.Exceptions;
using Xunit;

namespace PrintLogApi.IntegrationTests.Auth0;

/// <summary>
/// The pending-deletion job deletes the Auth0 account before the database rows and repeats the
/// whole user on the next run when a database step fails, so deleting an account that is already
/// gone must succeed.
/// </summary>
public class DeleteUserTests
{
    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DeleteUser_DeletedOrAlreadyGone_Succeeds(HttpStatusCode status)
    {
        var handler = new StubHandler
        {
            Responder = req => req.RequestUri!.AbsolutePath == "/oauth/token"
                ? Auth0TestHarness.TokenResponse()
                : new HttpResponseMessage(status),
        };

        await Auth0TestHarness.CreateService(handler).DeleteUser("auth0|gone");

        var delete = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.EndsWith("/api/v2/users/auth0%7Cgone", delete.RequestUri!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteUser_ServerError_Throws()
    {
        var handler = new StubHandler
        {
            Responder = req => req.RequestUri!.AbsolutePath == "/oauth/token"
                ? Auth0TestHarness.TokenResponse()
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        };

        await Assert.ThrowsAsync<Auth0ApiException>(() => Auth0TestHarness.CreateService(handler).DeleteUser("auth0|user"));
    }
}
