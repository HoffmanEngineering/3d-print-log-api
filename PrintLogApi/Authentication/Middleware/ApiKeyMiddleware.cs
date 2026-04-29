using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using PrintLogApi.Services;

namespace PrintLogApi.Authentication.Middleware
{
    public class ApiKeyMiddleware
    {
        private static readonly PathString _apiPath = new("/api");
        private readonly RequestDelegate _next;

        public ApiKeyMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, IUserApiKeyService userApiKeyService)
        {
            if (context.Request.Path.StartsWithSegments(_apiPath))
            {
                if (context.Request.Headers.TryGetValue("X-Api-Key", out var headerKey))
                {
                    await ValidateApiKey(context, _next, headerKey, userApiKeyService);
                }
                else if (context.Request.Query.TryGetValue("api_key", out var queryKey))
                {
                    await ValidateApiKey(context, _next, queryKey, userApiKeyService);
                }
                else
                {
                    await _next.Invoke(context);
                }
            }
            else
            {
                await _next.Invoke(context);
            }
        }

        private async Task ValidateApiKey(HttpContext context, RequestDelegate next, string key, IUserApiKeyService userApiKeyService)
        {
            long userId;
            try
            {
                userId = await userApiKeyService.GetUserIdByApiKey(key);
            }
            catch (ApiKeyIsNotValidException)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsync("Invalid API Key");
                return;
            }

            var identity = new GenericIdentity("API");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
            context.User = new GenericPrincipal(identity, new[] { "ApiUser" });

            await userApiKeyService.UpdateApiKeyLastUsed(key);

            await next(context);
        }
    }
}
