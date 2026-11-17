namespace PrintLogApi.Middleware;

/// <summary>
/// Marks a write that <see cref="ConnectorWriteRateLimiting"/> does not charge to the
/// credential's bucket (#200). The per-user "api" policy still applies to it.
///
/// For a liveness signal only, never for a write that changes print data: the bucket exists to
/// bound a backfill, and an exempt route is a way around it.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ExemptFromConnectorWriteBudgetAttribute : Attribute
{
}
