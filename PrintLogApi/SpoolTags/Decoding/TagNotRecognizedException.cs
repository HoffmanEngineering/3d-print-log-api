namespace PrintLogApi.SpoolTags.Decoding;

/// <summary>The tag holds data, but not in a format this decoder understands.</summary>
public sealed class TagNotRecognizedException : Exception
{
    public TagNotRecognizedException()
        : base("The tag format is not recognized.")
    {
    }

    public TagNotRecognizedException(string message)
        : base(message)
    {
    }
}
