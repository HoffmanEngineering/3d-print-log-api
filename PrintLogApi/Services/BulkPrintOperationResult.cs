using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services;

/// <summary>
/// A print that a bulk delete actually removed. Carries the creation date because the
/// long-standing <c>PrintDeleted</c> telemetry event reports it, and an event whose
/// properties depend on which endpoint did the deleting is not one report can read.
/// </summary>
/// <param name="Id">The deleted print's id.</param>
/// <param name="CreatedDate">When the print was originally created.</param>
public sealed record DeletedPrintInfo(long Id, DateTime CreatedDate);

/// <summary>
/// A bulk operation's response plus the users whose cached summaries it invalidated.
/// The set is not just the caller: bulk update lets a printer's owner edit a print
/// somebody else created, and the summary cache is keyed by the print's owner.
/// </summary>
/// <param name="Response">The body to return to the client.</param>
/// <param name="AffectedUserIds">Every user whose cache must be invalidated.</param>
/// <param name="DeletedPrints">
/// Prints that were actually removed. Only meaningful for a delete, and deliberately not
/// the same as <c>Response.Succeeded</c>: a delete reports an already-missing id as
/// succeeded, and no telemetry should be emitted for a print that was not there.
/// </param>
public sealed record BulkPrintOperationResult(
    BulkPrintResultDto Response,
    IReadOnlyCollection<long> AffectedUserIds,
    IReadOnlyCollection<DeletedPrintInfo> DeletedPrints)
{
    public BulkPrintOperationResult(BulkPrintResultDto response, IReadOnlyCollection<long> affectedUserIds)
        : this(response, affectedUserIds, []) { }
}
