using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PrintLogApi.Controllers;
using PrintLogApi.Exceptions;
using PrintLogApi.Models.DTOs;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class ConnectedAgentsControllerTests
    {
        private sealed class FakeAuth0Service : IAuth0Service
        {
            public string LastListUser;
            public (string User, string Grant)? LastRevoke;
            public IReadOnlyList<ConnectedAgentDto> GrantsToReturn = new List<ConnectedAgentDto>();
            public bool ThrowNotFoundOnRevoke;

            public Task<IReadOnlyList<ConnectedAgentDto>> ListMcpGrants(string authUserId, CancellationToken ct)
            {
                LastListUser = authUserId;
                return Task.FromResult(GrantsToReturn);
            }

            public Task RevokeMcpGrant(string authUserId, string grantId, CancellationToken ct)
            {
                LastRevoke = (authUserId, grantId);
                if (ThrowNotFoundOnRevoke)
                {
                    throw new NotFoundException("not found");
                }
                return Task.CompletedTask;
            }

            public Task DeleteUser(string oauthUserId) => Task.CompletedTask;
            public Task<string> GetManagementApiBearerToken() => Task.FromResult(string.Empty);
            public Task<string> GetUserEmail(string oauthUserId, CancellationToken ct) => Task.FromResult<string>(null);
        }

        private static ConnectedAgentsController CreateController(FakeAuth0Service service, string subject)
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Upn, subject) }, "test"));
            return new ConnectedAgentsController(service)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = principal },
                },
            };
        }

        [Fact]
        public async Task Get_ReturnsCallersAgents()
        {
            var service = new FakeAuth0Service
            {
                GrantsToReturn = new List<ConnectedAgentDto>
                {
                    new("grant-1", "client-1", new[] { "read:printdata" }),
                },
            };
            var controller = CreateController(service, "auth0|caller");

            var result = await controller.GetConnectedAgents(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var agents = Assert.IsAssignableFrom<IReadOnlyList<ConnectedAgentDto>>(ok.Value);
            Assert.Single(agents);
            Assert.Equal("auth0|caller", service.LastListUser);
        }

        [Fact]
        public async Task Delete_RevokesWithCallerSubject()
        {
            var service = new FakeAuth0Service();
            var controller = CreateController(service, "auth0|caller");

            var result = await controller.RevokeConnectedAgent("grant-1", CancellationToken.None);

            Assert.IsType<NoContentResult>(result);
            Assert.Equal(("auth0|caller", "grant-1"), service.LastRevoke);
        }

        [Fact]
        public async Task Delete_ForeignGrant_Returns404()
        {
            var service = new FakeAuth0Service { ThrowNotFoundOnRevoke = true };
            var controller = CreateController(service, "auth0|caller");

            var result = await controller.RevokeConnectedAgent("someone-elses-grant", CancellationToken.None);

            Assert.IsType<NotFoundResult>(result);
        }
    }
}
