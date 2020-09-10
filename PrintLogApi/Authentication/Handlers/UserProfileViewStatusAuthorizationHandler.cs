using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using PrintLogApi.Models;

namespace PrintLogApi.Authentication.Handlers
{
    public class UserProfileViewStatusAuthorizationHandler :
    AuthorizationHandler<PublicOrUnlistedUserProfileRequirement, User>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
                                                       PublicOrUnlistedUserProfileRequirement requirement,
                                                       User resource)
        {
            if (resource?.ViewStatus == User.ProfileViewStatus.Public || resource?.ViewStatus == User.ProfileViewStatus.Unlisted)
            {
                context.Succeed(requirement);
            }
            else
            {
                if (context.User.Identity.IsAuthenticated)
                {
                    var userId = long.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier).Value);


                    if (userId == resource?.Id)
                    {
                        context.Succeed(requirement);
                    }
                }

            }



            return Task.CompletedTask;
        }
    }

    public class PublicOrUnlistedUserProfileRequirement : IAuthorizationRequirement { }
}

