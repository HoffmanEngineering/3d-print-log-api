using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using PrintLogApi.Users;

namespace PrintLogApi.Authentication
{
    public class ClaimsTransformer : IClaimsTransformation
    {
        private readonly UserService userService;
        public ClaimsTransformer(UserService userService)
        {
            this.userService = userService;
        }

        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var existingClaimsIdentity = (ClaimsIdentity)principal.Identity;

            var authUserId = existingClaimsIdentity.Claims.Where(c => c.Type == ClaimTypes.Upn).FirstOrDefault().Value;

            var localUserId = await userService.GetLocalUserIdByAuthUserId(authUserId);

            if (localUserId == 0)
            {
                var newUser = await userService.CreateUserFromAuthId(authUserId);
                existingClaimsIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, newUser.Id.ToString()));
            }
            else
            {
                existingClaimsIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, localUserId.ToString()));
            }


            return principal;

            // Initialize a new list of claims for the new identity
            //var claims = new List<Claim>();

            //// Find the user in the DB
            //// Add as many role claims as they have roles in the DB
            //IdentityUser user = await _context.Users.FirstOrDefaultAsync(u => u.UserName.Equals(currentUserName, StringComparison.CurrentCultureIgnoreCase));
            //if (user != null)
            //{
            //    var rolesNames = from ur in _context.UserRoles.Where(p => p.UserId == user.Id)
            //                     from r in _context.Roles
            //                     where ur.RoleId == r.Id
            //                     select r.Name;

            //    claims.AddRange(rolesNames.Select(x => new Claim(ClaimTypes.Role, x)));
            //}

            //// Build and return the new principal
            //var newClaimsIdentity = new ClaimsIdentity(claims, existingClaimsIdentity.AuthenticationType);
            //return new ClaimsPrincipal(newClaimsIdentity);
        }
    }
}
