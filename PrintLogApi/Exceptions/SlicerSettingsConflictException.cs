namespace PrintLogApi.Exceptions;

/// <summary>
/// A connector sent slicer settings for a print whose settings came from a slicer plugin (#164).
/// </summary>
public class SlicerSettingsConflictException(string message) : Exception(message);
