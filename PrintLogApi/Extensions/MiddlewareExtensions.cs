using System;
using Microsoft.AspNetCore.Builder;
using PrintLogApi.Authentication.Middleware;

namespace PrintLogApi.Extensions
{
    public static class MiddlewareExtensions
    {
        public static IApplicationBuilder UseApiKeyAuthentication(
            this IApplicationBuilder builder)
        {
            if (builder is null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            return builder.UseMiddleware<ApiKeyMiddleware>();
        }
    }
}
