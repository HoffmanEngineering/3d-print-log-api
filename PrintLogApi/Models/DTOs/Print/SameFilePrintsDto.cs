namespace PrintLogApi.Models.DTOs.Print;

/// <summary>
/// The prints of one file (UI #10): every print of the caller's whose file hash matches the
/// requested print's. Each print stays its own entry; this only counts and lists them.
/// </summary>
public class SameFilePrintsDto
{
    /// <summary>
    /// How many of your prints share this print's file hash, this print included. 0 when the print
    /// has no file hash, because then nothing identifies its file.
    /// </summary>
    public int Count { get; set; }

    /// <summary>The other prints of the same file, newest first, at most the requested limit.</summary>
    public List<SameFilePrintDto>? Prints { get; set; }
}
