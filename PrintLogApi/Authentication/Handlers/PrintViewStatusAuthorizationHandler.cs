using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using PrintLogApi.Models;

namespace PrintLogApi.Authentication.Handlers
{
    public class PrintViewStatusAuthorizationHandler :
    AuthorizationHandler<PublicOrCreatorRequirement, Print>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
                                                       PublicOrCreatorRequirement requirement,
                                                       Print resource)
        {
            if (resource?.ViewStatus == Print.PrintViewStatus.Public || resource?.ViewStatus == Print.PrintViewStatus.Unlisted)
            {
                context.Succeed(requirement);
            }
            else
            {
                // Both dereferences are null-forgiven rather than guarded so the generated IL is
                // unchanged: a null Identity or a missing NameIdentifier claim already threw here
                // before nullable analysis was turned on. Replacing either with a null check would
                // silently turn a 500 into an authorization denial. Tracked in #57.
                if (context.User.Identity!.IsAuthenticated)
                {
                    var userId = long.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);


                    if (userId == resource?.CreatedById)
                    {
                        context.Succeed(requirement);
                    }
                }

            }



            return Task.CompletedTask;
        }
    }

    public class PublicOrCreatorRequirement : IAuthorizationRequirement { }
}

