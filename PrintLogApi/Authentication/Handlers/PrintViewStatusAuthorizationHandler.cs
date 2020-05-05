using Microsoft.AspNetCore.Authorization;
using PrintLogApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

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
            } else
            {
                if (context.User.Identity.IsAuthenticated)
                {
                    var userId = long.Parse(context.User.FindFirst(ClaimTypes.NameIdentifier).Value);


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

