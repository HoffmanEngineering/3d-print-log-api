namespace PrintLogApi.SpoolTags.Decoding;

/// <summary>The tag is formatted for NDEF but holds no data.</summary>
public sealed class BlankTagException : Exception
{
    public BlankTagException()
        : base("The tag is blank.")
    {
    }

    public BlankTagException(string message)
        : base(message)
    {
    }
}
