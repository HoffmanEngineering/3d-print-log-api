using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly RequestDelegate _next;

        public ApiKeyMiddleware(RequestDelegate next)
        {
            _next = next;
        }
        
        public async Task InvokeAsync(HttpContext context, IUserApiKeyService userApiKeyService)
        {
            if (context.Request.Path.StartsWithSegments(new PathString("/api")))
            {
                //Let's check if this is an API Call
                if (context.Request.Headers.Keys.Contains("X-Api-Key", StringComparer.InvariantCultureIgnoreCase))
                {
                    // validate the supplied API key
                    // Validate it
                    var headerKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
                    await ValidateApiKey(context, _next, headerKey, userApiKeyService);
                }
                else if (context.Request.Query.Keys.Contains("api_key", StringComparer.InvariantCultureIgnoreCase))
                {
                    var queryStringKey = context.Request.Query["api_key"].FirstOrDefault();
                    await ValidateApiKey(context, _next, queryStringKey, userApiKeyService);
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
            // validate it here
            bool valid;

            long userId = -1;
            try
            {
                userId = await userApiKeyService.GetUserIdByApiKey(key);
                valid = true;
            }
            catch (ApiKeyIsNotValidException)
            {
                valid = false;
            }

            if (!valid)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsync("Invalid API Key");
            }
            else
            {
                var identity = new GenericIdentity("API");
                

                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));

                var principal = new GenericPrincipal(identity, new[] { "ApiUser" });

                context.User = principal;
                await next(context);
            }
        }
    }
}
