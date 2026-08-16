using System.Globalization;
using System.Security.Claims;
using System.Security.Principal;

namespace PrintLogApi.Extensions;

public static class IdentityExtensions
{
    public static long? GetUserId(this IPrincipal identity)
    {
        var claim = ((ClaimsPrincipal)identity).FindFirst(ClaimTypes.NameIdentifier);

        if (claim == null)
        {
            return null;
        }

        try
        {
            return long.Parse(claim.Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }


    }
}
