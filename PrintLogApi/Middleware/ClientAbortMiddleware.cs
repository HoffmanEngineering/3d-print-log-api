using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PrintLogApi.Middleware
{
    /// <summary>
    /// Swallows the exceptions a request throws on its way down after the caller has already
    /// hung up, so a user navigating away mid-load does not register as a server fault.
    ///
    /// ASP.NET Core already does this for <see cref="OperationCanceledException"/>, but that only
    /// covers half the cases. Cancelling a <c>SqlCommand</c> while its reader is mid-fetch makes
    /// Microsoft.Data.SqlClient throw <see cref="InvalidOperationException"/> with the message
    /// "Operation cancelled by user." instead — a long-standing quirk of its error mapping, and
    /// not a type the framework recognises as an abort. Which of the two you get is a race
    /// between the token tripping at an await boundary and it tripping inside the TDS read, so
    /// the same aborted request surfaces either way at random. Without this, the SqlClient half
    /// becomes a 500 and a stack trace in the logs.
    ///
    /// The long analytics aggregates are where this shows up in practice, but the middleware is
    /// deliberately global: any endpoint that awaits a slow query can hit the same race.
    /// </summary>
    public class ClientAbortMiddleware
    {
        /// <summary>
        /// nginx's non-standard "client closed request". Nothing reads it — the socket is gone —
        /// but it keeps aborted requests out of the 5xx bucket in access logs and metrics.
        /// </summary>
        private const int ClientClosedRequest = 499;

        private readonly RequestDelegate _next;
        private readonly ILogger<ClientAbortMiddleware> _logger;

        public ClientAbortMiddleware(RequestDelegate next, ILogger<ClientAbortMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex) when (IsClientAbort(context, ex))
            {
                // Only OperationCanceledException is unambiguously an abort. The rest matched on
                // the connection state alone, so record what was actually thrown rather than
                // discarding it — that is the difference between a quiet abort and a genuine
                // fault that happened to coincide with one.
                if (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Request {Method} {Path} faulted after the client disconnected.",
                        context.Request.Method,
                        context.Request.Path);
                }

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = ClientClosedRequest;
                }
            }
        }

        /// <summary>
        /// The connection state is the real test, not the exception type. Anything thrown once
        /// <see cref="HttpContext.RequestAborted"/> has fired is heading for a response nobody is
        /// listening to, so there is nothing to be gained by letting it become a 500.
        ///
        /// Deliberately not matched on the SqlClient message: it comes from a localizable
        /// resource string, so a machine with different culture settings would silently stop
        /// matching and the 500s would come back.
        /// </summary>
        private static bool IsClientAbort(HttpContext context, Exception ex) =>
            context.RequestAborted.IsCancellationRequested
            && ex is OperationCanceledException or InvalidOperationException;
    }
}
