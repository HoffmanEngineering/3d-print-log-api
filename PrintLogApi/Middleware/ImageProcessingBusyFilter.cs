using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using PrintLogApi.Exceptions;

namespace PrintLogApi.Middleware;

/// <summary>
/// Answers <see cref="ImageProcessingBusyException"/> from any action with 503, a
/// <c>Retry-After</c> and <c>code</c> <c>image_processing_busy</c>, so every upload endpoint
/// says "try again" the same way instead of a 500. Registered globally on MVC.
/// </summary>
public sealed class ImageProcessingBusyFilter(ProblemDetailsFactory problems) : IExceptionFilter
{
    public const string Code = "image_processing_busy";

    /// <summary>A few maximum-size images' worth of processing: long enough for the line to move.</summary>
    public const int RetryAfterSeconds = 10;

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ImageProcessingBusyException busy)
        {
            return;
        }

        var problem = problems.CreateProblemDetails(context.HttpContext, StatusCodes.Status503ServiceUnavailable, detail: busy.Message);
        problem.Extensions["code"] = Code;
        context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }
}
