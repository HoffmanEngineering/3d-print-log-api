using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace PrintLogApi.IntegrationTests.Support;

/// <summary>Thrown by a test to make <see cref="TestRetryingExecutionStrategy"/> retry.</summary>
public sealed class TransientTestException : Exception
{
    public TransientTestException() : base("Simulated transient failure.") { }
}

/// <summary>
/// A retrying execution strategy for SQLite, which has none. It retries only on
/// <see cref="TransientTestException"/>, so a test can fail one attempt on purpose and observe
/// what the next one does — the shape of SqlServerRetryingExecutionStrategy that MCP creates use.
/// </summary>
public sealed class TestRetryingExecutionStrategy(DbContext context)
    : ExecutionStrategy(context, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
{
    protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestException;

    protected override TimeSpan? GetNextDelay(Exception lastException) =>
        base.GetNextDelay(lastException) is null ? null : TimeSpan.Zero;
}
