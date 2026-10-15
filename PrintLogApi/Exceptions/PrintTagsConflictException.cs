namespace PrintLogApi.Exceptions;

/// <summary>
/// Another request changed the same print's tags at the same moment, and this one lost. The
/// caller should read the print's tags again and retry.
/// </summary>
public class PrintTagsConflictException(Exception? innerException = null)
    : Exception("The print's tags were changed at the same time. Reload them and try again.", innerException)
{
}
