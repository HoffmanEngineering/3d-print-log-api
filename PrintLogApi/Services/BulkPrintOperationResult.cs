using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services;

/// <summary>
/// A bulk operation's response plus the users whose cached summaries it invalidated.
/// The set is not just the caller: bulk update lets a printer's owner edit a print
/// somebody else created, and the summary cache is keyed by the print's owner.
/// </summary>
/// <param name="Response">The body to return to the client.</param>
/// <param name="AffectedUserIds">Every user whose cache must be invalidated.</param>
/// <param name="DeletedPrintIds">
/// Prints that were actually removed. Only meaningful for a delete, and deliberately not
/// the same as <c>Response.Succeeded</c>: a delete reports an already-missing id as
/// succeeded, and no telemetry should be emitted for a print that was not there.
/// </param>
public sealed record BulkPrintOperationResult(
    BulkPrintResultDto Response,
    IReadOnlyCollection<long> AffectedUserIds,
    IReadOnlyCollection<long> DeletedPrintIds)
{
    public BulkPrintOperationResult(BulkPrintResultDto response, IReadOnlyCollection<long> affectedUserIds)
        : this(response, affectedUserIds, []) { }
}
