using System;

namespace PrintLogApi.Extensions
{
    /// <summary>
    /// Marks an action that serves a single media asset — a print or project image — and so gets
    /// the larger "media" rate limiting budget instead of the general per-caller one.
    ///
    /// These endpoints are fanned out by the browser, not by a person: one gallery page can request
    /// up to 100 images at once because the page size allows it, and the requests arrive in a burst
    /// of a few seconds. Counting them against the same budget as ordinary data calls would mean a
    /// user paging through an uncached gallery three times looked identical to abuse.
    ///
    /// Applied as metadata rather than as a separate [EnableRateLimiting] policy so that a single
    /// partition function in Startup decides the budget: attribute-versus-convention precedence in
    /// the rate limiting middleware is subtle, and getting it wrong fails open.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class MediaEndpointAttribute : Attribute
    {
    }
}
