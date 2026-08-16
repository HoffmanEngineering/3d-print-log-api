using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PrintLogApi.Extensions;

namespace PrintLogApi.Authentication;

/// <summary>
/// Succeeds only when the principal has a non-blank <c>Upn</c> (Auth0 subject) and a
/// resolvable internal user id (<see cref="IdentityExtensions.GetUserId"/>). Fails closed
/// otherwise so MCP dispatch never runs for an unmapped caller.
/// </summary>
public class McpUserHandler : AuthorizationHandler<McpUserRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, McpUserRequirement requirement)
    {
        var upn = context.User.FindFirst(ClaimTypes.Upn)?.Value;
        if (string.IsNullOrWhiteSpace(upn))
        {
            return Task.CompletedTask;
        }

        if (context.User.GetUserId() is null)
        {
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
