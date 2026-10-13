namespace PrintLogApi.Exceptions;

/// <summary>
/// A claim (#165) that cannot apply: the print is already linked to another job or is not
/// Printing, or the job is already logged as another of the user's prints, named by
/// <see cref="ConflictingPrintId"/>.
/// </summary>
public class PrintClaimConflictException(string message, long? conflictingPrintId = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public long? ConflictingPrintId { get; } = conflictingPrintId;
}
