namespace PrintLogApi.Exceptions;

/// <summary>
/// A tag UID is already on another of the user's spools (#147). Carries that spool so the
/// response can name it and a client can link to it.
/// </summary>
public class TagUidConflictException(string tagUid, Guid filamentId, string? filamentDisplayName, Exception? innerException = null)
    : Exception($"Tag {tagUid} is already on another spool: {filamentDisplayName ?? filamentId.ToString()}.", innerException)
{
    public string TagUid { get; } = tagUid;
    public Guid FilamentId { get; } = filamentId;
    public string? FilamentDisplayName { get; } = filamentDisplayName;
}
