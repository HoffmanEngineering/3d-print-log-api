using PrintLogApi.Authentication.Middleware;
using PrintLogApi.Middleware;

namespace PrintLogApi.Extensions;

public static class MiddlewareExtensions
{
    public static IApplicationBuilder UseClientAbortHandling(
        this IApplicationBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        return builder.UseMiddleware<ClientAbortMiddleware>();
    }

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
